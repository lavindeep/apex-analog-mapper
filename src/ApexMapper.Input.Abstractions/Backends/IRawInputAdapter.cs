namespace ApexMapper.Input.Abstractions.Backends;

public interface IRawInputAdapter : IInputBackend
{
    event EventHandler<RawInputDeviceChanged>? DeviceChanged;
}
