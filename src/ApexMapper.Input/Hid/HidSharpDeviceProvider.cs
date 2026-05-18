using System.IO;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Hid;

/// <summary>
/// Enumerates HID devices via HidSharp, filters them against a
/// <see cref="DeviceAdapterDescriptor"/>, and opens them as <see cref="IHidDevice"/>
/// instances. HidSharp is the only HID backend that depends on this project; if we
/// ever swap to raw P/Invoke this class is the seam.
/// </summary>
public sealed class HidSharpDeviceProvider : IDeviceEnumerator
{
    private readonly DeviceAdapterDescriptor _descriptor;

    public HidSharpDeviceProvider(DeviceAdapterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptor = descriptor;
    }

    public IReadOnlyList<DiscoveredDevice> Enumerate()
    {
        var matches = new List<DiscoveredDevice>();
        var hidDevices = HidSharp.DeviceList.Local.GetHidDevices(
            _descriptor.Match.VendorId,
            _descriptor.Match.ProductId);

        foreach (var dev in hidDevices)
        {
            if (!UsagePageMatches(dev, _descriptor.InterfaceSelector.UsagePage))
            {
                continue;
            }

            var identity = new DeviceIdentity(
                VendorId: dev.VendorID,
                ProductId: dev.ProductID,
                SerialNumber: TryGet(dev.GetSerialNumber),
                ManufacturerName: TryGet(dev.GetManufacturer),
                ProductName: TryGet(dev.GetProductName));

            matches.Add(new DiscoveredDevice(identity, dev.DevicePath, SupportsAnalog: _descriptor.Capabilities.Analog));
        }
        return matches;
    }

    public IHidDevice? Open(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var hidDev = HidSharp.DeviceList.Local
            .GetHidDevices()
            .FirstOrDefault(d => string.Equals(d.DevicePath, device.DevicePath, StringComparison.OrdinalIgnoreCase));
        return hidDev is null ? null : new HidSharpDeviceAdapter(hidDev, device);
    }

    /// <summary>
    /// Returns true when <paramref name="wantUsagePage"/> is null (no constraint) or
    /// when any top-level collection on the device advertises that usage page.
    /// Failures reading the report descriptor (rare, permissions-bound) drop the
    /// device from the match set rather than crash the enumeration.
    /// </summary>
    private static bool UsagePageMatches(HidSharp.HidDevice dev, ushort? wantUsagePage)
    {
        if (wantUsagePage is not ushort want)
        {
            return true;
        }

        try
        {
            var descriptor = dev.GetReportDescriptor();
            foreach (var item in descriptor.DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    var page = (ushort)((usage >> 16) & 0xFFFF);
                    if (page == want)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGet(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }
}

internal sealed class HidSharpDeviceAdapter : IHidDevice
{
    private readonly HidSharp.HidDevice _hidDev;
    private readonly DiscoveredDevice _discovered;

    public HidSharpDeviceAdapter(HidSharp.HidDevice hidDev, DiscoveredDevice discovered)
    {
        ArgumentNullException.ThrowIfNull(hidDev);
        ArgumentNullException.ThrowIfNull(discovered);
        _hidDev = hidDev;
        _discovered = discovered;
    }

    public DeviceIdentity Identity => _discovered.Identity;
    public string DevicePath => _discovered.DevicePath;

    public IHidStream Open()
    {
        // Non-exclusive open (HidSharp's default). HidPollLoop catches the exception
        // and transitions to FaultedAnalog, so the bad path is reported, not crashed.
        if (!_hidDev.TryOpen(out var stream))
        {
            throw new IOException(
                $"failed to open hid device '{_discovered.Identity.ProductName ?? _discovered.DevicePath}'");
        }
        return new HidSharpStreamAdapter(stream);
    }
}
