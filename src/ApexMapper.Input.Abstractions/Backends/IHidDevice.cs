using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Backends;

public interface IHidDevice
{
    DeviceIdentity Identity { get; }
    string DevicePath { get; }
    IHidStream Open();
}
