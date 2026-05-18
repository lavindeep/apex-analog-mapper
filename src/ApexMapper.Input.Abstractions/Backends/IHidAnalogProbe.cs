using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Backends;

public interface IHidAnalogProbe : IInputBackend
{
    DeviceIdentity Device { get; }
    DeviceAdapterDescriptor Adapter { get; }
    bool IsHealthy { get; }
    IDisposable SubscribeRaw(KeyId key, Action<float> onRawNormalized);
}
