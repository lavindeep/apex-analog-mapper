namespace ApexMapper.Windows.Input;

/// <summary>
/// Follows the foreground window for the selected game and keeps the
/// <see cref="ForegroundFlag"/> the hook reads. <see cref="ForegroundTracker"/> is the
/// real one; the seam exists so the session's reactions to focus changes have
/// deterministic tests.
///
/// Contract on a transition: on gain <see cref="Changed"/> runs before the flag flips,
/// on loss after it drops. Handlers run on the source's thread and must not wait for it.
/// </summary>
public interface IForegroundSource : IDisposable
{
    /// <summary>Full path of the selected game's executable; null tracks nothing.</summary>
    string? GamePath { get; set; }

    /// <summary>The last resolved foreground window.</summary>
    ForegroundInfo Current { get; }

    /// <summary>Raised when the game gained or lost focus, with the new state.</summary>
    event Action<ForegroundInfo>? Changed;

    /// <summary>Exceptions caught on the source's thread, handlers included.</summary>
    int HandlerFaults { get; }

    /// <summary>Following focus right now; false before start, after stop, or when its thread ended early.</summary>
    bool IsRunning { get; }

    void Start();

    /// <summary>Stops following and clears the flag. Returns whether the source's thread has exited.</summary>
    bool Stop();
}
