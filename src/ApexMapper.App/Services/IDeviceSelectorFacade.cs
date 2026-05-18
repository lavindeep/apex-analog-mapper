using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.App.Services;

/// <summary>
/// App-side abstraction over <see cref="ApexMapper.Input.Abstractions.Devices.DeviceSelector"/>.
/// The concrete selector operates on <see cref="DiscoveredDevice"/> objects and
/// does not carry stable Guid identifiers — that mapping lives here so the
/// ViewModel and tests remain decoupled from the Phase-2 concrete class.
/// </summary>
public interface IDeviceSelectorFacade
{
    /// <summary>Stable deterministic Guid for the currently primary device, or null if none.</summary>
    Guid? PrimaryId { get; }

    /// <summary>Enumerate all devices currently known to the selector (connected + cached disconnected).</summary>
    IReadOnlyList<DeviceFacadeEntry> ListAll();

    /// <summary>Make the device with <paramref name="id"/> the primary selection and persist.</summary>
    void SelectPrimary(Guid id);

    /// <summary>Re-enumerate connected devices and fire <see cref="TopologyChanged"/> for adds/removes.</summary>
    void Refresh();

    /// <summary>
    /// Raised on the calling thread when topology changes.
    /// The event args carry the full new connected set.
    /// Tests may fire this synchronously; production must marshal to UI thread before updating the VM.
    /// </summary>
    event EventHandler<TopologyChangedEventArgs>? TopologyChanged;
}

/// <summary>A flattened, Guid-keyed snapshot of a single device entry.</summary>
public sealed record DeviceFacadeEntry(
    Guid Id,
    string DisplayName,
    ushort Vid,
    ushort Pid,
    bool IsConnected,
    bool IsPrimary);

/// <summary>Event args for <see cref="IDeviceSelectorFacade.TopologyChanged"/>.</summary>
public sealed class TopologyChangedEventArgs : EventArgs
{
    public IReadOnlyList<DeviceFacadeEntry> Devices { get; }

    public TopologyChangedEventArgs(IReadOnlyList<DeviceFacadeEntry> devices)
        => Devices = devices;
}
