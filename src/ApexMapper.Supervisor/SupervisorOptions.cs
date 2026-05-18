namespace ApexMapper.Supervisor;

public sealed record SupervisorOptions
{
    public TimeSpan ControlInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan HeartbeatGapBeforeZero { get; init; } = TimeSpan.FromMilliseconds(1000);
}
