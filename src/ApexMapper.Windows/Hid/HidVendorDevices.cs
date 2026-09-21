using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Native;
using HidSharp;

namespace ApexMapper.Windows.Hid;

/// <summary>
/// The only file that touches HidSharp. Finds the Apex Pro vendor interface (usage
/// page 0xFFC0, usage 0x0001, 65-byte input and output reports) among the interfaces
/// of the keyboard with the given container id, and opens it non-exclusively so GG
/// keeps working beside the mapper. Exactly one match or nothing: two matches would
/// mean two boards share a container id, which is not a case worth guessing at.
/// </summary>
public static class HidVendorDevices
{
    private const uint VendorUsage = 0xFFC0_0001u;

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
            list.Add(new HidInterfaceInfo(device.DevicePath, (ushort)device.ProductID, container.Value, SafeName(device), IsVendorInterface(device)));
        }
        return list;
    }

    /// <summary>Opens the vendor interface of the given keyboard, or null when it is absent or not exactly one.</summary>
    public static IVendorStream? Open(Guid containerId)
    {
        HidDevice? match = null;
        var matches = 0;
        foreach (var device in DeviceList.Local.GetHidDevices(KnownKeyboards.SteelSeriesVendorId))
        {
            if (!IsVendorInterface(device) || CfgMgr32.ContainerIdOf(device.DevicePath) != containerId)
            {
                continue;
            }
            match = device;
            matches++;
        }
        if (matches != 1 || match is null)
        {
            return null;
        }
        var options = new OpenConfiguration();
        options.SetOption(OpenOption.Exclusive, false);
        if (!match.TryOpen(options, out var stream))
        {
            return null;
        }
        stream.ReadTimeout = VendorInterface.TimeoutMs;
        stream.WriteTimeout = VendorInterface.TimeoutMs;
        return new HidSharpStream(stream);
    }

    private static bool IsVendorInterface(HidDevice device)
    {
        try
        {
            if (device.GetMaxInputReportLength() != SensorProtocol.ReportLength || device.GetMaxOutputReportLength() != SensorProtocol.ReportLength)
            {
                return false;
            }
            foreach (var item in device.GetReportDescriptor().DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    if (usage == VendorUsage)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

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

/// <summary>One HID interface of a SteelSeries device.</summary>
public sealed record HidInterfaceInfo(string Path, ushort ProductId, Guid ContainerId, string ProductName, bool IsVendorInterface);
