namespace ApexMapper.Supervisor;

public sealed record SupervisorOptions
{
    public TimeSpan ControlInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan HeartbeatGapBeforeZero { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>How long the server may sit with no connected session — measured
    /// from start until the first connection, and from each session end until the
    /// next — before its accept loop retires itself. Generous next to the tray
    /// adapter's reconnect ladder (capped at 2 s), so a live tray reconnects long
    /// before the window closes; the tray also respawns the supervisor on every
    /// enable, so a retired supervisor is restarted transparently.</summary>
    public TimeSpan IdleExitTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
