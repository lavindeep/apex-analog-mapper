namespace ApexMapper.Supervisor;

/// <summary>
/// Watchdog over client liveness. <see cref="NotifyAlive"/> records a sign of
/// life (any known frame counts — control frames prove liveness just as
/// heartbeats do); when the silence since the last sign of life reaches the
/// configured gap, <see cref="GapDetected"/> is raised exactly once for the
/// monitor's lifetime. A monitor guards a single session and is never reused:
/// after the gap fires, <see cref="NotifyAlive"/> cannot resurrect it.
///
/// Thread-safe: <see cref="NotifyAlive"/> (read-loop thread) races the timer
/// thread and <see cref="Dispose"/>. State is guarded by one lock;
/// <see cref="GapDetected"/> is raised outside it.
/// </summary>
public sealed class HeartbeatMonitor : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _gap;
    private readonly object _lock = new();

    private ITimer? _timer;
    private long _lastAliveTimestamp;
    private bool _started;
    private bool _gapFired;
    private bool _disposed;

    public HeartbeatMonitor(TimeProvider timeProvider, TimeSpan gap)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (gap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gap), gap, "The heartbeat gap must be positive.");
        }

        _gap = gap;
    }

    /// <summary>Raised at most once, when the silence threshold is reached.</summary>
    public event Action? GapDetected;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The monitor is already started.");
            }

            _started = true;
            _lastAliveTimestamp = _timeProvider.GetTimestamp();
            _timer = _timeProvider.CreateTimer(static state => ((HeartbeatMonitor)state!).OnTimer(), this, _gap, Timeout.InfiniteTimeSpan);
        }
    }

    public void NotifyAlive()
    {
        lock (_lock)
        {
            if (_disposed || _gapFired)
            {
                return;
            }

            _lastAliveTimestamp = _timeProvider.GetTimestamp();
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    private void OnTimer()
    {
        var fire = false;
        lock (_lock)
        {
            if (_disposed || _gapFired)
            {
                return;
            }

            TimeSpan remaining = _gap - _timeProvider.GetElapsedTime(_lastAliveTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                _gapFired = true;
                fire = true;
            }
            else
            {
                // A sign of life arrived since the timer was armed: push the
                // deadline out to exactly one gap after it.
                _timer?.Change(remaining, Timeout.InfiniteTimeSpan);
            }
        }

        if (fire)
        {
            GapDetected?.Invoke();
        }
    }
}
