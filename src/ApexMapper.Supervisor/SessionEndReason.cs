namespace ApexMapper.Supervisor;

/// <summary>Why a supervisor session ended. Whatever the reason, the pad was
/// zeroed and disconnected before the session completed.</summary>
public enum SessionEndReason
{
    /// <summary>The client closed the connection cleanly.</summary>
    PeerDisconnected,

    /// <summary>No known frame arrived within the configured heartbeat gap.</summary>
    HeartbeatGap,

    /// <summary>The connection faulted (transport or protocol failure, or the
    /// pad rejected a submit).</summary>
    Faulted,

    /// <summary>The client sent a panic frame.</summary>
    Panic,

    /// <summary>The owner cancelled the session (supervisor stopping).</summary>
    Shutdown,
}
