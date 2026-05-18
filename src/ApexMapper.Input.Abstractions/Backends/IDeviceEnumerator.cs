namespace ApexMapper.Input.Abstractions.Backends;

public interface IDeviceEnumerator
{
    IReadOnlyList<DiscoveredDevice> Enumerate();
}
