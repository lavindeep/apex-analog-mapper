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

    /// <summary>
    /// True when the current selection came from an automatic identity match
    /// that had more than one indistinguishable candidate (same VID/PID and
    /// product, no serial to tell them apart). The tie breaks
    /// deterministically on ordinal device-path order; the UI can surface
    /// this flag so the user confirms the right unit. Cleared by an explicit
    /// <see cref="Select"/> or <see cref="Unselect"/>.
    /// </summary>
    public bool AmbiguousMatch { get; private set; }

    public event EventHandler<DeviceTopologyChanged>? Changed;

    public void Initialize()
    {
        _discovered.Clear();
        _discovered.AddRange(_enumerator.Enumerate());
        _lastRegistry = _loadRegistry();

        if (_lastRegistry.SelectedDevice is { } savedIdentity)
        {
            SelectedDevice = FindMatch(savedIdentity, out var ambiguous);
            AmbiguousMatch = ambiguous;
        }
    }

    public void Refresh()
    {
        var current = _enumerator.Enumerate();
        var previous = _discovered.ToArray();

        var removed = new List<DiscoveredDevice>();
        foreach (var prev in previous)
        {
            if (!current.Any(device => SameSource(device, prev)))
            {
                removed.Add(prev);
            }
        }

        var added = new List<DiscoveredDevice>();
        foreach (var cur in current)
        {
            if (!previous.Any(device => SameSource(device, cur)))
            {
                added.Add(cur);
            }
        }

        _discovered.Clear();
        _discovered.AddRange(current);

        DiscoveredDevice? detachedSelection = null;
        if (SelectedDevice is { } sel)
        {
            SelectedDevice = current.FirstOrDefault(device => SameSource(device, sel));
            if (SelectedDevice is null)
                detachedSelection = sel;
        }

        foreach (var device in removed)
        {
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Detached, device));
        }

        if (detachedSelection is { } unselected)
        {
            // The selection is absent, not forgotten: the persisted identity
            // survives so a later attach can rebind it. Only an explicit
            // Unselect clears persistence.
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Unselected, unselected));
        }

        foreach (var device in added)
        {
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Attached, device));
        }

        if (SelectedDevice is null &&
            _lastRegistry.SelectedDevice is { } saved &&
            FindMatch(saved, out var ambiguous) is { } rebound)
        {
            SelectedDevice = rebound;
            AmbiguousMatch = ambiguous;
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, rebound));
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
        AmbiguousMatch = false;
        Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, device));
        PersistSelection(device.Identity);
    }

    public void Unselect()
    {
        var previous = SelectedDevice;
        if (previous is null && _lastRegistry.SelectedDevice is null) return;

        SelectedDevice = null;
        AmbiguousMatch = false;
        if (previous is not null)
        {
            Changed?.Invoke(this, new DeviceTopologyChanged(DeviceTopologyChangeKind.Unselected, previous));
        }
        PersistSelection(null);
    }

    private void PersistSelection(DeviceIdentity? identity)
    {
        var next = _lastRegistry with { SelectedDevice = identity };
        _saveRegistry(next);
        _lastRegistry = next;
    }

    public IReadOnlyList<KeyCalibration> GetCalibrations(DiscoveredDevice device, string firmware)
    {
        var registry = _lastRegistry;
        return !string.IsNullOrWhiteSpace(device.PhysicalDeviceId)
            && string.Equals(device.PhysicalDeviceId, registry.CalibrationDeviceId, StringComparison.OrdinalIgnoreCase)
            && firmware == registry.CalibrationFirmware
            ? registry.Calibrations : Array.Empty<KeyCalibration>();
    }

    /// <summary>Saves measurements for the selected physical keyboard without losing them on later selection changes.</summary>
    public void SaveCalibrations(string physicalDeviceId, string firmware, IReadOnlyList<KeyCalibration> calibrations)
    {
        if (string.IsNullOrWhiteSpace(physicalDeviceId) || SelectedDevice is not { } selected
            || !string.Equals(selected.PhysicalDeviceId, physicalDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected keyboard changed. Open calibration again.");
        if (string.IsNullOrWhiteSpace(firmware)) throw new ArgumentException("Firmware is required.", nameof(firmware));
        ArgumentNullException.ThrowIfNull(calibrations);
        var copy = calibrations.ToArray();
        if (copy.Select(c => c.Key).Distinct().Count() != copy.Length || copy.Any(c =>
            !float.IsFinite(c.RestValue) || !float.IsFinite(c.MaxPressValue) || !float.IsFinite(c.NoiseBand)
            || c.RestValue is < 0 or > 4095 || c.MaxPressValue is < 0 or > 4095
            || MathF.Abs(c.MaxPressValue - c.RestValue) < 100
            || c.NoiseBand <= 0 || c.NoiseBand >= MathF.Abs(c.MaxPressValue - c.RestValue)))
            throw new ArgumentException("Each key needs distinct released and fully pressed readings.", nameof(calibrations));
        var next = _lastRegistry with
        {
            Calibrations = copy,
            CalibrationDeviceId = physicalDeviceId,
            CalibrationFirmware = firmware,
        };
        _saveRegistry(next);
        _lastRegistry = next;
    }

    // Display metadata can change without disconnecting the input source.
    private static bool SameSource(DiscoveredDevice a, DiscoveredDevice b)
        => a.Identity == b.Identity
        && a.DevicePath == b.DevicePath
        && a.SupportsAnalog == b.SupportsAnalog;

    private DiscoveredDevice? FindMatch(DeviceIdentity saved, out bool ambiguous)
    {
        DiscoveredDevice? best = null;
        var matches = 0;
        foreach (var device in _discovered)
        {
            if (!IdentityMatches(device.Identity, saved))
            {
                continue;
            }

            matches++;
            if (best is null || string.CompareOrdinal(device.DevicePath, best.DevicePath) < 0)
            {
                best = device;
            }
        }

        ambiguous = matches > 1;
        return best;
    }

    /// <summary>
    /// Saved-identity match rule: VendorId and ProductId must be equal;
    /// ProductName must be ordinally equal (null only matches null); serials
    /// must be ordinally equal when both sides have one, and a device with no
    /// serial matches a saved identity with no serial on VID/PID + product
    /// alone. A serial present on exactly one side is NOT a match: if a
    /// firmware update changes serial exposure we cannot distinguish "same
    /// unit" from "another unit of the same model", and auto-driving the
    /// wrong keyboard is worse than requiring one manual re-select.
    /// </summary>
    private static bool IdentityMatches(DeviceIdentity a, DeviceIdentity b)
    {
        if (a.VendorId != b.VendorId) return false;
        if (a.ProductId != b.ProductId) return false;
        if (!string.Equals(a.ProductName, b.ProductName, StringComparison.Ordinal)) return false;
        return string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.Ordinal);
    }
}
