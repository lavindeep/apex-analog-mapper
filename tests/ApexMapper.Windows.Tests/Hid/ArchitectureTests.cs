using System.Reflection;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Hid;

public class ArchitectureTests
{
    /// <summary>
    /// The command allowlist lives in SensorRequest.WriteTo and VendorInterface is the
    /// only caller that reaches a device. Keeping HidSharp in one file is what makes
    /// that reviewable: nothing else can open a stream and write around the allowlist.
    /// </summary>
    [Fact]
    public void Only_the_vendor_device_file_touches_HidSharp()
    {
        var root = RepoRoot();
        var windows = Path.Combine(root, "src", "ApexMapper.Windows");
        var offenders = Directory.EnumerateFiles(windows, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("HidSharp", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(windows, f))
            .ToList();

        Assert.Equal([Path.Combine("Hid", "HidVendorDevices.cs")], offenders);
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<MethodBase> AllMethods(Type type) =>
        type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));

    /// <summary>
    /// Only VendorInterface calls IVendorStream.Write: every method body in the
    /// assembly (compiler-generated closures included) is scanned for a call or
    /// callvirt to that method's token. The type is internal, so nothing outside the
    /// assembly can reach it either.
    /// </summary>
    [Fact]
    public void Only_the_vendor_interface_calls_the_vendor_stream_write()
    {
        var assembly = typeof(VendorInterface).Assembly;
        var write = typeof(IVendorStream).GetMethod(nameof(IVendorStream.Write))!;
        Assert.False(typeof(IVendorStream).IsPublic);
        var token = BitConverter.GetBytes(write.MetadataToken);
        var callers = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in AllMethods(type))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null)
                {
                    continue;
                }
                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] is 0x28 or 0x6F && il.AsSpan(i + 1, 4).SequenceEqual(token))
                    {
                        callers.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        Assert.Equal(["ApexMapper.Windows.Hid.VendorInterface.Exchange"], callers.Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>No type other than VendorInterface stores or receives a stream; the one factory returns a fresh one.</summary>
    [Fact]
    public void Only_the_vendor_interface_holds_a_vendor_stream()
    {
        var stream = typeof(IVendorStream);
        var holders = new List<string>();
        foreach (var type in typeof(VendorInterface).Assembly.GetTypes())
        {
            holders.AddRange(type.GetFields(All).Where(f => stream.IsAssignableFrom(f.FieldType)).Select(f => $"{type.FullName}.{f.Name}"));
            holders.AddRange(AllMethods(type).Where(m => m.GetParameters().Any(p => stream.IsAssignableFrom(p.ParameterType))).Select(m => $"{type.FullName}.{m.Name}"));
        }

        Assert.Equal(["ApexMapper.Windows.Hid.VendorInterface..ctor", "ApexMapper.Windows.Hid.VendorInterface._stream"], holders.Order(StringComparer.Ordinal));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApexAnalogMapper.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Solution root not found above the test directory.");
    }
}
