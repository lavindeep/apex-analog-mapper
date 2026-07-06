namespace ApexMapper.Supervisor.Tests;

/// <summary>
/// Minimal virtual clock for driving TimeProvider-based components without
/// wall-clock waits. <see cref="Advance"/> moves time forward and fires due
/// timer callbacks synchronously, in chronological order; a callback that
/// re-arms its timer (via <see cref="ITimer.Change"/>) inside the same advance
/// fires again if the new due time is still within the window.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            timer.ScheduleLocked(_now, dueTime);
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Number of timers currently armed with a due time. Lets a test wait
    /// until a component running on another thread has scheduled its timer before
    /// advancing the clock.</summary>
    public int ScheduledTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.DueAt is not null);
            }
        }
    }

    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + delta;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers
                    .Where(t => t.DueAt is not null && t.DueAt <= target)
                    .OrderBy(t => t.DueAt)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    break;
                }

                _now = next.DueAt!.Value;
                next.ClearDueLocked();
            }

            // Fired outside the provider lock so a callback may create, re-arm,
            // or dispose timers without deadlocking.
            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            // Clearing the due time must happen under the provider lock: Dispose
            // can race Advance, which reads DueAt inside its locked region.
            timer.ClearDueLocked();
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        internal ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        internal DateTimeOffset? DueAt { get; private set; }

        internal void ScheduleLocked(DateTimeOffset now, TimeSpan dueTime) =>
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;

        internal void ClearDueLocked() => DueAt = null;

        internal void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_provider._lock)
            {
                ScheduleLocked(_provider._now, dueTime);
            }

            return true;
        }

        public void Dispose() => _provider.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
