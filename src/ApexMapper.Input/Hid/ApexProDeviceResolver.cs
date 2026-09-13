using System.IO;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.RawInput;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Hid;

/// <summary>Finds one sensor interface in the selected keyboard's Windows container.</summary>
public static class ApexProDeviceResolver
{
    public static IHidDevice? Resolve(DiscoveredDevice selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Identity.VendorId != 0x1038 || selected.Identity.ProductId != 0x1614
            || selected.PhysicalDeviceId is null)
        {
            return null;
        }

        var container = WindowsKeyboardMetadata.Read(selected.DevicePath).PhysicalDeviceId;
        if (container is null || !string.Equals(container, selected.PhysicalDeviceId,
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        HidSharp.HidDevice? match = null;
        foreach (var device in HidSharp.DeviceList.Local.GetHidDevices(0x1038, 0x1614))
        {
            if (!IsSensorInterface(device, container))
            {
                continue;
            }
            if (match is not null)
            {
                return null;
            }
            match = device;
        }
        return match is null ? null : new SensorDevice(match, selected.Identity);
    }

    private static bool IsSensorInterface(HidSharp.HidDevice device, string container)
    {
        try
        {
            return device.GetMaxInputReportLength() == 65
                && device.GetMaxOutputReportLength() == 65
                && string.Equals(WindowsKeyboardMetadata.Read(device.DevicePath).PhysicalDeviceId,
                    container, StringComparison.OrdinalIgnoreCase)
                && device.GetReportDescriptor().DeviceItems
                    .Any(item => item.Usages.GetAllValues().Contains(0xFFC00001u));
        }
        catch
        {
            return false;
        }
    }

    private sealed class SensorDevice(HidSharp.HidDevice device, DeviceIdentity identity) : IHidDevice
    {
        public DeviceIdentity Identity => identity;
        public string DevicePath => device.DevicePath;

        public IHidStream Open()
        {
            if (!device.TryOpen(out var stream))
            {
                throw new IOException("Could not open the selected Apex Pro sensor interface.");
            }
            try
            {
                stream.ReadTimeout = 150;
                stream.WriteTimeout = 150;
                return new HidSharpStreamAdapter(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }
}
