using ApexMapper.App.Services;
using ApexMapper.Core;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Foreground;

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal time-provider that lets tests advance a virtual clock and fire
/// any pending timer callbacks synchronously.  Avoids taking a dependency on
/// Microsoft.Extensions.TimeProvider.Testing — the logic is small enough that
/// an inline helper is clearer.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    // Each active timer is represented by a single entry: (callback, dueTime).
    // We use a list rather than a sorted structure because the number of
    // concurrent timers is always ≤ 1 in ForegroundWatcher.
    private readonly List<(TimerCallback callback, DateTimeOffset dueAt)> _timers = new();
    private readonly object _lock = new();

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var due = _now + dueTime;
        var entry = new ManualTimer(this, callback, due);
        lock (_lock)
            _timers.Add((callback, due));
        return entry;
    }

    /// <summary>
    /// Advances the virtual clock by <paramref name="delta"/> and fires all
    /// callbacks whose due time has been reached, in chronological order.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        _now += delta;

        List<TimerCallback> toFire;
        lock (_lock)
        {
            toFire = _timers
                .Where(t => t.dueAt <= _now)
                .Select(t => t.callback)
                .ToList();
            _timers.RemoveAll(t => t.dueAt <= _now);
        }

        foreach (var cb in toFire)
            cb(null);
    }

    internal void RemoveTimer(ManualTimer timer)
    {
        lock (_lock)
            _timers.RemoveAll(t => t.callback == timer.Callback);
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        internal TimerCallback Callback { get; }

        public ManualTimer(ManualTimeProvider provider, TimerCallback callback, DateTimeOffset dueAt)
        {
            _provider = provider;
            Callback = callback;
            _ = dueAt; // stored in parent list
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
            _provider.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Synthetic event source — events are fired by calling <see cref="Fire"/>.</summary>
internal sealed class FakeWindowEventSource : IWindowEventSource
{
    public bool Started  { get; private set; }
    public bool Stopped  { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler<WindowFocusEvent>? FocusChanged;

    public void Start()  => Started = true;
    public void Stop()   => Stopped = true;
    public void Dispose() => Disposed = true;

    public void Fire(IntPtr hwnd = default, uint pid = 1u)
    {
        var ev = new WindowFocusEvent(hwnd == default ? new IntPtr(0x1234) : hwnd, pid, DateTimeOffset.UtcNow);
        FocusChanged?.Invoke(this, ev);
    }
}

/// <summary>Probe that returns a programmable context per call.</summary>
internal sealed class FakeForegroundProbe : IForegroundProbe
{
    private readonly Queue<ForegroundContext?> _results = new();

    /// <summary>Enqueue contexts to be returned on successive Resolve calls.</summary>
    public void Enqueue(ForegroundContext? ctx) => _results.Enqueue(ctx);

    public int ResolveCallCount { get; private set; }

    public ForegroundContext? Resolve(IntPtr hwnd, uint processId)
    {
        ResolveCallCount++;
        return _results.Count > 0 ? _results.Dequeue() : MakeContext(processId);
    }

    private static ForegroundContext MakeContext(uint pid) =>
        new(@"C:\Games\SomeGame.exe", "Some Game", pid, null, DateTimeOffset.UtcNow);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class ForegroundWatcherDebounceTests
{
    private static ForegroundContext MakeCtx(string exe = @"C:\Games\Game.exe") =>
        new(exe, "Window", 42u, null, DateTimeOffset.UtcNow);

    // -----------------------------------------------------------------------
    // 1. Single event emits after debounce
    // -----------------------------------------------------------------------

    [Fact]
    public void Single_focus_event_emits_context_after_debounce()
    {
        var tp    = new ManualTimeProvider();
        var src   = new FakeWindowEventSource();
        var probe = new FakeForegroundProbe();
        var ctx   = MakeCtx();
        probe.Enqueue(ctx);

        using var watcher = new ForegroundWatcher(src, probe, tp);
        watcher.Start();

        ForegroundChangedEventArgs? received = null;
        watcher.ForegroundChanged += (_, e) => received = e;

        src.Fire();

        // Before debounce period — no emission.
        tp.Advance(TimeSpan.FromMilliseconds(499));
        received.Should().BeNull();

        // After debounce period — one emission.
        tp.Advance(TimeSpan.FromMilliseconds(1));
        received.Should().NotBeNull();
        received!.Context.Should().Be(ctx);
    }

    // -----------------------------------------------------------------------
    // 2. Multiple rapid events collapse to the last one
    // -----------------------------------------------------------------------

    [Fact]
    public void Multiple_focus_events_within_window_emit_only_last()
    {
        var tp    = new ManualTimeProvider();
        var src   = new FakeWindowEventSource();
        var probe = new FakeForegroundProbe();

        // Enqueue three contexts; only the third should be emitted.
        var ctx1 = MakeCtx(@"C:\Games\Alpha.exe");
        var ctx2 = MakeCtx(@"C:\Games\Beta.exe");
        var ctx3 = MakeCtx(@"C:\Games\Gamma.exe");
        probe.Enqueue(ctx1);
        probe.Enqueue(ctx2);
        probe.Enqueue(ctx3);

        using var watcher = new ForegroundWatcher(src, probe, tp);
        watcher.Start();

        var emitted = new List<ForegroundContext>();
        watcher.ForegroundChanged += (_, e) => emitted.Add(e.Context);

        // t=0: first event
        src.Fire(pid: 1u);
        // t=100ms: second event (resets timer)
        tp.Advance(TimeSpan.FromMilliseconds(100));
        src.Fire(pid: 2u);
        // t=300ms: third event (resets timer again)
        tp.Advance(TimeSpan.FromMilliseconds(200));
        src.Fire(pid: 3u);
        // t=800ms: well past debounce for the third event
        tp.Advance(TimeSpan.FromMilliseconds(500));

        emitted.Should().ContainSingle();
        // The third event was the pending one, so probe was called with pid=3.
        // We enqueued ctx3 last, so it should be that context.
        emitted[0].Should().Be(ctx3);
    }

    // -----------------------------------------------------------------------
    // 3. Two separated events each emit
    // -----------------------------------------------------------------------

    [Fact]
    public void Two_separated_focus_events_emit_twice()
    {
        var tp    = new ManualTimeProvider();
        var src   = new FakeWindowEventSource();
        var probe = new FakeForegroundProbe();

        var ctxA = MakeCtx(@"C:\Games\A.exe");
        var ctxB = MakeCtx(@"C:\Games\B.exe");
        probe.Enqueue(ctxA);
        probe.Enqueue(ctxB);

        using var watcher = new ForegroundWatcher(src, probe, tp);
        watcher.Start();

        var emitted = new List<ForegroundContext>();
        watcher.ForegroundChanged += (_, e) => emitted.Add(e.Context);

        // First event + debounce.
        src.Fire();
        tp.Advance(TimeSpan.FromMilliseconds(500));

        // Second event (well after first debounce window) + debounce.
        src.Fire();
        tp.Advance(TimeSpan.FromMilliseconds(500));

        emitted.Should().HaveCount(2);
        emitted[0].Should().Be(ctxA);
        emitted[1].Should().Be(ctxB);
    }

    // -----------------------------------------------------------------------
    // 4. Current reflects the last debounced context
    // -----------------------------------------------------------------------

    [Fact]
    public void Current_reflects_last_debounced_context()
    {
        var tp    = new ManualTimeProvider();
        var src   = new FakeWindowEventSource();
        var probe = new FakeForegroundProbe();
        var ctx   = MakeCtx();
        probe.Enqueue(ctx);

        using var watcher = new ForegroundWatcher(src, probe, tp);
        watcher.Start();

        watcher.Current.Should().Be(ForegroundContext.Empty);

        src.Fire();
        tp.Advance(TimeSpan.FromMilliseconds(500));

        watcher.Current.Should().Be(ctx);
    }

    // -----------------------------------------------------------------------
    // 5. Stop cancels a pending debounce
    // -----------------------------------------------------------------------

    [Fact]
    public void Stop_cancels_pending_debounce()
    {
        var tp    = new ManualTimeProvider();
        var src   = new FakeWindowEventSource();
        var probe = new FakeForegroundProbe();

        using var watcher = new ForegroundWatcher(src, probe, tp);
        watcher.Start();

        var emitted = new List<ForegroundContext>();
        watcher.ForegroundChanged += (_, e) => emitted.Add(e.Context);

        src.Fire();
        tp.Advance(TimeSpan.FromMilliseconds(250)); // halfway through debounce

        watcher.Stop();

        tp.Advance(TimeSpan.FromMilliseconds(1000)); // well past debounce

        emitted.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // 6. Probe returning null suppresses emission
    // -----------------------------------------------------------------------

    [Fact]
    public void Probe_returns_null_skips_emit()
    {
        var tp    = new ManualTimeProvider();
        var src   = new FakeWindowEventSource();
        var probe = new FakeForegroundProbe();
        probe.Enqueue(null); // probe signals "process not available"

        using var watcher = new ForegroundWatcher(src, probe, tp);
        watcher.Start();

        var emitted = new List<ForegroundContext>();
        watcher.ForegroundChanged += (_, e) => emitted.Add(e.Context);

        src.Fire();
        tp.Advance(TimeSpan.FromMilliseconds(500));

        emitted.Should().BeEmpty();
        watcher.Current.Should().Be(ForegroundContext.Empty); // unchanged
    }
}
