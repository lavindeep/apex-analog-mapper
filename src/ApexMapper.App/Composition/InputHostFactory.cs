using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Hid;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.App.Composition;

/// <summary>
/// Assembles the input pipeline (raw-input adapter, optional HID analog probe,
/// ring buffer, key-state store) into an <see cref="InputHost"/>. Invoked by
/// the composition root; the raw-input adapter hosts its own message-only
/// window on its pump thread, so no caller-supplied HWND is involved.
/// </summary>
public static class InputHostFactory
{
    /// <summary>
    /// Builds an <see cref="InputHost"/>. When <paramref name="hidDevice"/> and
    /// <paramref name="adapter"/> are both provided, an analog probe is constructed
    /// with the persisted per-key calibration list from the device registry, so
    /// calibrations captured by the wizard survive restarts; otherwise the host
    /// runs digital-only (raw input, no analog depth).
    /// </summary>
    public static InputHost Create(
        IRawInputAdapter rawInput,
        IHidDevice? hidDevice,
        DeviceAdapterDescriptor? adapter,
        int reportLength,
        DeviceSelector deviceSelector,
        Func<DeviceRegistry> loadRegistry,
        SpscRingBuffer<RawKeyEvent> ring,
        KeyStateStore store,
        ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(rawInput);
        ArgumentNullException.ThrowIfNull(deviceSelector);
        ArgumentNullException.ThrowIfNull(loadRegistry);
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(store);

        IHidAnalogProbe? probe = null;
        if (hidDevice is not null && adapter is not null)
        {
            var calibrations = loadRegistry().Calibrations;
            probe = new HidAnalogProbe(
                device: hidDevice,
                adapter: adapter,
                store: store,
                reportLength: reportLength,
                calibrations: calibrations is { Count: > 0 } ? calibrations : null);
        }

        return new InputHost(
            rawInput: rawInput,
            hidProbe: probe,
            deviceSelector: deviceSelector,
            ring: ring,
            store: store,
            log: log);
    }
}
