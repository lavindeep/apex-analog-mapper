namespace ApexMapper.Input.Abstractions.Backends;

public enum DeviceTopologyChangeKind
{
    Attached,
    Detached,
    Selected,
    Unselected,
}

public sealed record DeviceTopologyChanged(
    DeviceTopologyChangeKind ChangeKind,
    DiscoveredDevice Device);
