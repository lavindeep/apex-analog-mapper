using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Devices;

public sealed class DeviceSelector
{
    private readonly IDeviceEnumerator _enumerator;
    private readonly Func<DeviceRegistry> _loadRegistry;
    private readonly Action<DeviceRegistry> _saveRegistry;
    private readonly List<DiscoveredDevice> _discovered = new();
    private DeviceRegistry _lastRegistry = new(null, Array.Empty<KeyCalibration>());

    public DeviceSelector(
        IDeviceEnumerator enumerator,
        Func<DeviceRegistry> loadRegistry,
        Action<DeviceRegistry> saveRegistry)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _loadRegistry = loadRegistry ?? throw new ArgumentNullException(nameof(loadRegistry));
        _saveRegistry = saveRegistry ?? throw new ArgumentNullException(nameof(saveRegistry));
    }

    public IReadOnlyList<DiscoveredDevice> Discovered => _discovered;

    public DiscoveredDevice? SelectedDevice { get; private set; }

    public DeviceIdentity? SelectedIdentity => SelectedDevice?.Identity;

    public event EventHandler<DeviceTopologyChanged>? Changed;

    public void Initialize()
    {
        _discovered.Clear();
        _discovered.AddRange(_enumerator.Enumerate());
        _lastRegistry = _loadRegistry();

        if (_lastRegistry.SelectedDevice is { } savedIdentity)
        {
            foreach (var device in _discovered)
            {
                if (IdentityMatches(device.Identity, savedIdentity))
                {
                    SelectedDevice = device;
                    break;
                }
            }
        }
    }

    public void Refresh()
    {
        var current = _enumerator.Enumerate();
        var previous = _discovered.ToArray();

        var removed = new List<DiscoveredDevice>();
        foreach (var prev in previous)
        {
            if (!current.Contains(prev))
            {
                removed.Add(prev);
            }
        }

        var added = new List<DiscoveredDevice>();
        foreach (var cur in current)
        {
            if (!previous.Contains(cur))
            {
                added.Add(cur);
            }
        }

        _discovered.Clear();
        _discovered.AddRange(current);

        DiscoveredDevice? detachedSelection = null;
        if (SelectedDevice is { } sel && removed.Contains(sel))
        {
            detachedSelection = sel;
            SelectedDevice = null;
        }

        foreach (var device in removed)
        {
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Detached, device));
        }

        if (detachedSelection is { } unselected)
        {
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Unselected, unselected));
            PersistSelection(null);
        }

        foreach (var device in added)
        {
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Attached, device));
        }
    }

    public void Select(DiscoveredDevice device)
    {
        if (device is null) throw new ArgumentNullException(nameof(device));
        if (!_discovered.Contains(device))
        {
            throw new InvalidOperationException("Device is not in the discovered set.");
        }

        SelectedDevice = device;
        Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, device));
        PersistSelection(device.Identity);
    }

    public void Unselect()
    {
        if (SelectedDevice is not { } previous) return;

        SelectedDevice = null;
        Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Unselected, previous));
        PersistSelection(null);
    }

    private void PersistSelection(DeviceIdentity? identity)
    {
        var next = new DeviceRegistry(identity, _lastRegistry.Calibrations);
        _lastRegistry = next;
        _saveRegistry(next);
    }

    private static bool IdentityMatches(DeviceIdentity a, DeviceIdentity b)
    {
        if (a.VendorId != b.VendorId) return false;
        if (a.ProductId != b.ProductId) return false;
        return string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.Ordinal);
    }
}
