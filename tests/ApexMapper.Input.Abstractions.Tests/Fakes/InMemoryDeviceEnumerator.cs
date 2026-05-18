using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.Input.Abstractions.Tests.Fakes;

public sealed class InMemoryDeviceEnumerator : IDeviceEnumerator
{
    private readonly List<DiscoveredDevice> _devices;

    public InMemoryDeviceEnumerator(IEnumerable<DiscoveredDevice> initial)
    {
        _devices = new List<DiscoveredDevice>(initial);
    }

    public void Add(DiscoveredDevice device) => _devices.Add(device);

    public bool Remove(DiscoveredDevice device) => _devices.Remove(device);

    public IReadOnlyList<DiscoveredDevice> Enumerate() => _devices.ToArray();
}
