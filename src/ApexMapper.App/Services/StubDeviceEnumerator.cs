using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.App.Services;

/// <summary>
/// Stub <see cref="IDeviceEnumerator"/> that returns an empty device list.
/// Used until Phase 3 supplies the real Windows HID enumerator.
/// </summary>
internal sealed class StubDeviceEnumerator : IDeviceEnumerator
{
    public IReadOnlyList<DiscoveredDevice> Enumerate() =>
        Array.Empty<DiscoveredDevice>();
}
