using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Native;
using HidSharp;

namespace ApexMapper.Windows.Hid;

/// <summary>One HID interface of a SteelSeries device.</summary>
/// <param name="IsVendorInterface">The interface the sensor path opens: the vendor usage with 65-byte reports.</param>
/// <param name="VendorInputLength">The input report length of an interface with the vendor usage, whatever it is; zero for any other.</param>
/// <param name="VendorOutputLength">The output report length, likewise.</param>
public sealed record HidInterfaceInfo(string Path, ushort ProductId, Guid ContainerId, string ProductName, bool IsVendorInterface, int VendorInputLength = 0, int VendorOutputLength = 0);

/// <summary>What the selection rule looks at for one HID interface.</summary>
internal readonly record struct VendorCandidate(ushort ProductId, Guid ContainerId, bool HasVendorUsage, int InputLength, int OutputLength);

/// <summary>
/// The only file that touches HidSharp. Finds the Apex Pro vendor interface (usage
/// page 0xFFC0, usage 0x0001, 65-byte input and output reports) among the interfaces
/// of the keyboard with the given container id, and opens it non-exclusively so GG
/// keeps working beside the mapper. Only a known Apex Pro product id qualifies, so a
/// SteelSeries mouse or headset with a lookalike interface is never written to.
/// Exactly one match or nothing: two matches would mean two boards share a container
/// id, which is not a case worth guessing at.
/// </summary>
public static class HidVendorDevices
{
    private const uint VendorUsage = 0xFFC0_0001u;

    /// <summary>How long an open waits behind another handle before giving up. HidSharp's defaults are 3 s and 30 s.</summary>
    public const int OpenTimeoutMs = 300;

    /// <summary>Every SteelSeries HID interface with its container id, for discovery.</summary>
    public static IReadOnlyList<HidInterfaceInfo> SteelSeriesInterfaces()
    {
        var list = new List<HidInterfaceInfo>();
        foreach (var device in DeviceList.Local.GetHidDevices(KnownKeyboards.SteelSeriesVendorId))
        {
            var container = CfgMgr32.ContainerIdOf(device.DevicePath);
            if (container is null)
            {
                continue;
            }
            var c = Describe(device, container.Value);
            list.Add(c.HasVendorUsage
                ? new HidInterfaceInfo(device.DevicePath, c.ProductId, c.ContainerId, SafeName(device), IsSensorInterface(c), c.InputLength, c.OutputLength)
                : new HidInterfaceInfo(device.DevicePath, c.ProductId, c.ContainerId, SafeName(device), false));
        }
        return list;
    }

    /// <summary>The selection rule, pure: the index of the one candidate to open, or -1 when there is none or more than one.</summary>
    internal static int Select(ReadOnlySpan<VendorCandidate> candidates, Guid containerId)
    {
        var match = -1;
        for (var i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            if (c.ContainerId != containerId || !c.HasVendorUsage
                || c.InputLength != SensorProtocol.ReportLength || c.OutputLength != SensorProtocol.ReportLength
                || !KnownKeyboards.IsApexPro(c.ProductId))
            {
                continue;
            }
            if (match >= 0)
            {
                return -1;
            }
            match = i;
        }
        return match;
    }

    /// <summary>Opens the vendor interface of the given keyboard, or null when it is absent, ambiguous, or busy.</summary>
    internal static IVendorStream? Open(Guid containerId)
    {
        var devices = new List<HidDevice>();
        var candidates = new List<VendorCandidate>();
        foreach (var device in DeviceList.Local.GetHidDevices(KnownKeyboards.SteelSeriesVendorId))
        {
            var container = CfgMgr32.ContainerIdOf(device.DevicePath);
            if (container != containerId)
            {
                continue;
            }
            devices.Add(device);
            candidates.Add(Describe(device, container.Value));
        }
        var index = Select(candidates.ToArray(), containerId);
        if (index < 0)
        {
            return null;
        }
        var options = new OpenConfiguration();
        options.SetOption(OpenOption.Exclusive, false);
        options.SetOption(OpenOption.TimeoutIfInterruptible, OpenTimeoutMs);
        options.SetOption(OpenOption.TimeoutIfTransient, OpenTimeoutMs);
        if (!devices[index].TryOpen(options, out var stream))
        {
            return null;
        }
        stream.ReadTimeout = VendorInterface.TimeoutMs;
        stream.WriteTimeout = VendorInterface.TimeoutMs;
        return new HidSharpStream(stream);
    }

    private static VendorCandidate Describe(HidDevice device, Guid container)
    {
        try
        {
            var hasUsage = false;
            foreach (var item in device.GetReportDescriptor().DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    hasUsage |= usage == VendorUsage;
                }
            }
            return new VendorCandidate((ushort)device.ProductID, container, hasUsage, device.GetMaxInputReportLength(), device.GetMaxOutputReportLength());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return new VendorCandidate((ushort)device.ProductID, container, false, 0, 0);
        }
    }

    private static bool IsSensorInterface(VendorCandidate c) =>
        c.HasVendorUsage && c.InputLength == SensorProtocol.ReportLength && c.OutputLength == SensorProtocol.ReportLength;

    private static string SafeName(HidDevice device)
    {
        try
        {
            return device.GetProductName();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private sealed class HidSharpStream(HidStream stream) : IVendorStream
    {
        public int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);

        public void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

        public void Dispose() => stream.Dispose();
    }
}
