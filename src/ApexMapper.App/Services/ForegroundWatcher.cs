using ApexMapper.Core;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="IForegroundWatcher"/>.
/// Wraps an <see cref="IWindowEventSource"/> and an <see cref="IForegroundProbe"/>
/// and applies a 500 ms debounce so that rapid window switches only produce a
/// single <see cref="ForegroundChanged"/> event for the window that finally
/// held focus.
/// </summary>
/// <remarks>
/// Thread-safety: <see cref="IWindowEventSource"/> delivers events on whatever
/// thread the WinEvent hook runs on (typically the thread that called
/// <see cref="Start"/> — the WPF UI thread in production).  The lock guards
/// both the timer reset and the capture of the latest pending event so that
/// concurrent focus changes cannot slip through.
/// </remarks>
public sealed class ForegroundWatcher : IForegroundWatcher
{
    private const int DebounceMs = 500;

    private readonly IWindowEventSource _source;
    private readonly IForegroundProbe   _probe;
    private readonly TimeProvider       _time;
    private readonly object             _lock = new();

    private ITimer?          _debounceTimer;
    private WindowFocusEvent _pendingEvent;
    private bool             _started;
    private bool             _disposed;

    public ForegroundContext Current { get; private set; } = ForegroundContext.Empty;

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public ForegroundWatcher(
        IWindowEventSource source,
        IForegroundProbe   probe,
        TimeProvider?      time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(probe);
        _source = source;
        _probe  = probe;
        _time   = time ?? TimeProvider.System;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _source.FocusChanged += OnFocusChanged;
        }
        _source.Start();
    }

    public void Stop()
    {
        ITimer? timer;
        lock (_lock)
        {
            if (!_started) return;
            _started = false;
            _source.FocusChanged -= OnFocusChanged;
            timer = _debounceTimer;
            _debounceTimer = null;
        }

        timer?.Dispose();
        _source.Stop();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        _source.Dispose();
    }

    // -----------------------------------------------------------------------
    // Private
    // -----------------------------------------------------------------------

    private void OnFocusChanged(object? sender, WindowFocusEvent ev)
    {
        ITimer? oldTimer;
        lock (_lock)
        {
            if (!_started) return;
            _pendingEvent = ev;

            oldTimer = _debounceTimer;
            // Reschedule: create a new timer that fires once after DebounceMs.
            // Creating a new timer (rather than restarting) avoids races between
            // Change() and the callback on concurrent events.
            _debounceTimer = _time.CreateTimer(
                CommitDebounced,
                state: null,
                dueTime:  TimeSpan.FromMilliseconds(DebounceMs),
                period:   Timeout.InfiniteTimeSpan);
        }

        // Dispose the previous timer outside the lock to avoid potential
        // re-entrancy if disposal triggers a callback.
        oldTimer?.Dispose();
    }

    private void CommitDebounced(object? state)
    {
        WindowFocusEvent ev;
        lock (_lock)
        {
            if (!_started) return;
            ev = _pendingEvent;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        var ctx = _probe.Resolve(ev.Hwnd, ev.ProcessId);
        if (ctx is null) return; // probe rejected (process gone / access denied)

        lock (_lock)
        {
            Current = ctx;
        }

        ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(ctx));
    }
}
