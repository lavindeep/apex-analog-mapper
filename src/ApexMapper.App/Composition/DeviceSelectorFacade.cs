using System.Security.Cryptography;
using System.Text;
using ApexMapper.App.Services;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.App.Composition;

/// <summary>
/// Concrete <see cref="IDeviceSelectorFacade"/> that wraps
/// <see cref="DeviceSelector"/> (Phase 2) and bridges it to the App-layer
/// facade contract consumed by the tray and device-picker viewmodels.
///
/// Guid identity is derived deterministically by SHA-1-truncating the
/// string <c>vid:pid:serialnumber</c> to 16 bytes, then constructing
/// a <see cref="Guid"/> from those bytes.  The same identity always
/// produces the same Guid regardless of process restart or enumeration
/// order.
/// </summary>
public sealed class DeviceSelectorFacade : IDeviceSelectorFacade
{
    private readonly DeviceSelector _selector;

    public event EventHandler<TopologyChangedEventArgs>? TopologyChanged;

    public DeviceSelectorFacade(DeviceSelector selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _selector.Changed += OnSelectorChanged;
    }

    // -------------------------------------------------------------------------
    // IDeviceSelectorFacade
    // -------------------------------------------------------------------------

    public Guid? PrimaryId =>
        _selector.SelectedDevice is { } dev ? ToGuid(dev.Identity) : null;

    public IReadOnlyList<DeviceFacadeEntry> ListAll()
    {
        var primaryIdentity = _selector.SelectedDevice?.Identity;

        return _selector.Discovered
            .Select(d =>
            {
                var id = ToGuid(d.Identity);
                var isPrimary = primaryIdentity is { } p && IdentityMatches(d.Identity, p);
                return new DeviceFacadeEntry(
                    Id:          id,
                    DisplayName: BuildDisplayName(d.Identity),
                    Vid:         (ushort)d.Identity.VendorId,
                    Pid:         (ushort)d.Identity.ProductId,
                    IsConnected: true,
                    IsPrimary:   isPrimary);
            })
            .ToList();
    }

    public void SelectPrimary(Guid id)
    {
        var device = _selector.Discovered.FirstOrDefault(d => ToGuid(d.Identity) == id);
        if (device is not null)
            _selector.Select(device);
    }

    public void Refresh() => _selector.Refresh();

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void OnSelectorChanged(object? sender, DeviceTopologyChanged ev)
    {
        // Rebuild the full connected snapshot and raise our facade event.
        var entries = ListAll();
        TopologyChanged?.Invoke(this, new TopologyChangedEventArgs(entries));
    }

    /// <summary>
    /// Produces a deterministic <see cref="Guid"/> from a <see cref="DeviceIdentity"/>
    /// by computing SHA-1 of "vid:pid:serial" and using the first 16 bytes.
    /// </summary>
    internal static Guid ToGuid(DeviceIdentity identity)
    {
        var key = $"{identity.VendorId:x4}:{identity.ProductId:x4}:{identity.SerialNumber ?? string.Empty}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        // Take first 16 bytes of SHA-1 to form the Guid.
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string BuildDisplayName(DeviceIdentity id)
    {
        var product = id.ProductName ?? $"PID:{id.ProductId:X4}";
        var serial  = string.IsNullOrEmpty(id.SerialNumber) ? string.Empty : $" ({id.SerialNumber})";
        return $"{product}{serial}";
    }

    private static bool IdentityMatches(DeviceIdentity a, DeviceIdentity b)
        => a.VendorId  == b.VendorId
        && a.ProductId == b.ProductId
        && string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.Ordinal);
}
