using ApexMapper.Core.Keys;

namespace ApexMapper.Input.Abstractions.Backends;

/// <summary>Publishes selected-keyboard sensor input without doing HID I/O on the mapping tick.</summary>
public interface IAnalogInputSource : IInputBackend
{
    void SelectDevice(DiscoveredDevice? device);
    bool OwnsKey(KeyId key);
    void ApplyTo(KeyStateStore store);
    string? ReadinessError { get; }
}
