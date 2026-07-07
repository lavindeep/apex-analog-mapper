namespace ApexMapper.Output.Ipc;

/// <summary>
/// Cadence and reconnect policy for <see cref="SupervisorChannelAdapter"/>.
/// The control and heartbeat intervals mirror the supervisor's own option
/// defaults (100 ms control, 250 ms heartbeat); they are duplicated here
/// because the project dependency points the other way — the supervisor
/// references this assembly. Reconnect backoff doubles from
/// <see cref="ReconnectInitialDelay"/> up to <see cref="ReconnectMaxDelay"/>
/// and retries forever: the supervisor frees its pipe within about a second
/// of losing a client (the heartbeat gap), so a capped retry always gets
/// back in once the supervisor is reachable again.
/// </summary>
public sealed record SupervisorChannelOptions
{
    public TimeSpan ControlInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(2);
}
