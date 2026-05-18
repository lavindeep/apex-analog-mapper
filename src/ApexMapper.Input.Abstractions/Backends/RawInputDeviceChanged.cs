using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Backends;

public sealed record RawInputDeviceChanged(
    DeviceIdentity Device,
    bool Attached,
    string DevicePath);
