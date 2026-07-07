namespace ApexMapper.App.Services;

/// <summary>
/// Owns the enabled/disabled state of the whole mapping path: the key-state
/// store's held-key gate, the mapping engine, and the supervisor channel.
/// Every transition is fail-closed — a blocked or failed enable leaves output
/// off with the reason surfaced, and disable/panic always zero locally before
/// anything else.
/// </summary>
public interface IMappingSession
{
    bool IsEnabled { get; }

    /// <summary>Raised on every state transition or surfaced condition, on the
    /// thread that performed the transition (marshal in UI consumers).</summary>
    event EventHandler<MappingSessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Runs the enable flow: pre-flight (a blocker keeps output disabled),
    /// anti-cheat scan (a positive or unavailable scan requires explicit user
    /// confirmation — never silent), Steam advisory, supervisor spawn, channel
    /// connect, engine enable. Returns true when mapping is enabled. Never
    /// throws (other than cancellation): every failure surfaces through
    /// <see cref="StateChanged"/> and returns false.
    /// </summary>
    Task<bool> EnableAsync(CancellationToken ct);

    /// <summary>
    /// Disables mapping: engine off (its next tick zeroes the slot), held keys
    /// gated, then a best-effort zero+disconnect to the supervisor. The local
    /// off always completes even when the channel misbehaves.
    /// </summary>
    Task DisableAsync(CancellationToken ct);

    /// <summary>
    /// Panic-path local off: engine disabled and held keys gated synchronously,
    /// without waiting on any lock or IO, and without touching the channel (the
    /// panic frame is the caller's job). Never throws.
    /// </summary>
    void ForceLocalOff(string reason);
}

public sealed class MappingSessionStateChangedEventArgs(bool isEnabled, string? message) : EventArgs
{
    public bool IsEnabled { get; } = isEnabled;

    /// <summary>User-facing status or error text, or null for a silent transition.</summary>
    public string? Message { get; } = message;
}
