using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Backends;

public sealed record DiscoveredDevice(
    DeviceIdentity Identity,
    string DevicePath,
    bool SupportsAnalog,
    string? DisplayName = null,
    string? PhysicalDeviceId = null);
