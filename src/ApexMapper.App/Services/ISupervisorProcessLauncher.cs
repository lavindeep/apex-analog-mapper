namespace ApexMapper.App.Services;

/// <summary>
/// Ensures a supervisor process for the current session is running. Launching
/// is idempotent by design: the supervisor holds a per-session mutex and a
/// second launch for the same session exits 0 immediately, so callers may
/// invoke this on every enable without bookkeeping.
/// </summary>
public interface ISupervisorProcessLauncher
{
    /// <summary>
    /// Starts (or confirms) the supervisor for this session. Returns
    /// <c>null</c> on success, or a user-facing error message when the
    /// supervisor executable is missing or the launch failed — in which case
    /// output must stay disabled (fail-closed) and the message surfaced.
    /// </summary>
    string? EnsureRunning();
}
