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
