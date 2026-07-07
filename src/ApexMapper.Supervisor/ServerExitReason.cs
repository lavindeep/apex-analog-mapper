namespace ApexMapper.Supervisor;

/// <summary>Why the server's accept loop completed. Whatever the reason, no
/// session is left running once the loop has unwound.</summary>
public enum ServerExitReason
{
    /// <summary><see cref="SupervisorServer.StopAsync"/> (or disposal) ended the loop.</summary>
    Stopped,

    /// <summary>A full <see cref="SupervisorOptions.IdleExitTimeout"/> window
    /// elapsed with no connected session, so the server retired itself instead
    /// of lingering; the tray respawns a supervisor on the next enable.</summary>
    IdleTimeout,
}
