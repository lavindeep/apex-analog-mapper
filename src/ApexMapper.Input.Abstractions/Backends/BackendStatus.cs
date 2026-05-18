namespace ApexMapper.Input.Abstractions.Backends;

public enum BackendStatus
{
    Stopped,
    Starting,
    Running,
    Degraded,
    FaultedDigital,
    FaultedAnalog,
    Stopping,
}
