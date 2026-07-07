using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Engine;

public class MappingEngineTests
{
    private static readonly KeyId Throttle = KeyId.FromScanCode(0x11);
    private static readonly KeyId SteerLeft = KeyId.FromScanCode(0x1E);
    private static readonly KeyId SteerRight = KeyId.FromScanCode(0x20);

    private static Profile MakeProfile(
        string id = "p1",
        BindingTarget singleTarget = BindingTarget.RightTrigger) => new(
        id,
        id,
        new DeviceMatcher(0x1038, 0x161C, null, null),
        new GameMatcher(null, null, null),
        ActivationPolicy.Default,
        new[] { new SingleKeyBinding(Throttle, singleTarget, LinearCurve.Instance, 120f, 0f) },
        new[] { new AxisPairBinding(SteerLeft, SteerRight, BindingTarget.LeftStickX, LinearCurve.Instance, 80f, 80f, SocdMode.Neutral) },
        Notes: null);

    private sealed class CapturingSink : IPadStateSink
    {
        public int PushCount { get; private set; }

        public VirtualPadState Last { get; private set; }

        public void Push(in VirtualPadState state)
        {
            Last = state;
            PushCount++;
        }
    }

    [Fact]
    public void Tick_without_a_profile_pushes_a_zero_state()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        store.Set(Throttle, 1f, KeyProvenance.Analog);

        engine.TickOnce(1f);

        sink.PushCount.Should().Be(1);
        sink.Last.Should().Be(default(VirtualPadState));
    }

    [Fact]
    public void Tick_maps_store_state_through_the_active_profile()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetProfile(MakeProfile());

        // Analog provenance bypasses ramps, so a single tick reflects the raw depth.
        store.Set(Throttle, 0.75f, KeyProvenance.Analog);
        store.Set(SteerRight, 1f, KeyProvenance.Analog);

        engine.TickOnce(1f);

        sink.Last.RightTrigger.Should().Be(0.75f);
        sink.Last.LeftStickX.Should().Be(1f);
    }

    [Fact]
    public void Profile_swap_takes_effect_on_the_next_tick()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetProfile(MakeProfile("a", BindingTarget.RightTrigger));
        store.Set(Throttle, 0.6f, KeyProvenance.Analog);

        engine.TickOnce(1f);
        sink.Last.RightTrigger.Should().Be(0.6f);
        sink.Last.LeftTrigger.Should().Be(0f);

        engine.SetProfile(MakeProfile("b", BindingTarget.LeftTrigger));

        engine.TickOnce(1f);
        sink.Last.LeftTrigger.Should().Be(0.6f);
        sink.Last.RightTrigger.Should().Be(0f, "the swapped-out binding must stop driving its old target");
    }

    [Fact]
    public void Clearing_the_profile_returns_the_pad_to_zero()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetProfile(MakeProfile());
        store.Set(Throttle, 1f, KeyProvenance.Analog);
        engine.TickOnce(1f);
        sink.Last.RightTrigger.Should().Be(1f);

        engine.SetProfile(null);
        engine.TickOnce(1f);

        sink.Last.Should().Be(default(VirtualPadState));
    }

    [Fact]
    public void Disable_pushes_a_zero_state_exactly_once_then_stays_silent()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetProfile(MakeProfile());
        store.Set(Throttle, 1f, KeyProvenance.Analog);

        engine.TickOnce(1f);
        sink.Last.RightTrigger.Should().Be(1f);
        var pushesBeforeDisable = sink.PushCount;

        engine.SetEnabled(false);

        // The first disabled tick must push zero — disable must never freeze the
        // last non-zero state at the sink (stuck-input class).
        engine.TickOnce(1f);
        sink.PushCount.Should().Be(pushesBeforeDisable + 1);
        sink.Last.Should().Be(default(VirtualPadState));

        // Further disabled ticks are idle: no additional pushes.
        engine.TickOnce(1f);
        engine.TickOnce(1f);
        sink.PushCount.Should().Be(pushesBeforeDisable + 1);
    }

    [Fact]
    public void Reenable_resumes_pushing_mapped_state()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetProfile(MakeProfile());
        store.Set(Throttle, 0.5f, KeyProvenance.Analog);

        engine.SetEnabled(false);
        engine.TickOnce(1f);
        sink.Last.Should().Be(default(VirtualPadState));

        engine.SetEnabled(true);
        engine.TickOnce(1f);

        sink.Last.RightTrigger.Should().Be(0.5f);
        engine.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Engine_disabled_before_any_tick_pushes_zero_once()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetEnabled(false);

        engine.TickOnce(1f);
        engine.TickOnce(1f);

        sink.PushCount.Should().Be(1);
        sink.Last.Should().Be(default(VirtualPadState));
    }

    [Fact]
    public async Task Started_engine_ticks_on_its_own_thread_and_stop_joins_bounded()
    {
        var store = new KeyStateStore();
        var sink = new ConcurrentSink();
        await using var engine = new MappingEngine(store, sink);
        engine.SetProfile(MakeProfile());
        // Written before the loop thread starts, so the dictionary-backed store
        // is safe here: Thread.Start establishes the happens-before edge.
        store.Set(Throttle, 1f, KeyProvenance.Analog);

        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sink.Count >= 50);
        sink.Last.RightTrigger.Should().Be(1f);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.StopAsync(CancellationToken.None);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "stop must join the tick thread within its bound");

        // The loop never ends leaving a non-zero state latched at the sink.
        sink.Last.Should().Be(default(VirtualPadState));

        var countAfterStop = sink.Count;
        await Task.Delay(50);
        sink.Count.Should().Be(countAfterStop, "a stopped engine must not keep ticking");
    }

    [Fact]
    public async Task Start_twice_is_idempotent_and_the_engine_keeps_ticking()
    {
        var store = new KeyStateStore();
        var sink = new ConcurrentSink();
        await using var engine = new MappingEngine(store, sink);

        await engine.StartAsync(CancellationToken.None);
        await engine.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => sink.Count >= 10);
    }

    [Fact]
    public async Task Stop_without_start_is_a_no_op()
    {
        var store = new KeyStateStore();
        var sink = new ConcurrentSink();
        await using var engine = new MappingEngine(store, sink);

        Func<Task> act = async () => await engine.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Double_dispose_is_safe()
    {
        var store = new KeyStateStore();
        var sink = new ConcurrentSink();
        var engine = new MappingEngine(store, sink);
        await engine.StartAsync(CancellationToken.None);

        await engine.DisposeAsync();
        Func<Task> act = async () => await engine.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public void PreTick_hook_runs_before_mapping_so_its_writes_land_in_the_same_tick()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var engine = new MappingEngine(
            store,
            sink,
            preTick: () => store.Set(Throttle, 0.75f, KeyProvenance.Analog));
        engine.SetProfile(MakeProfile());

        engine.TickOnce(1f);

        sink.Last.RightTrigger.Should().Be(
            0.75f, "the drain hook must run before the pipeline reads the store, not after");
    }

    [Fact]
    public void PreTick_hook_runs_on_disabled_ticks_so_input_keeps_draining()
    {
        var store = new KeyStateStore();
        var sink = new CapturingSink();
        var calls = 0;
        var engine = new MappingEngine(store, sink, preTick: () => calls++);
        engine.SetEnabled(false);

        engine.TickOnce(1f);
        engine.TickOnce(1f);

        calls.Should().Be(2, "key releases must keep flowing into the store while disabled");
    }

    private sealed class ConcurrentSink : IPadStateSink
    {
        private readonly object _lock = new();
        private VirtualPadState _last;
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public VirtualPadState Last
        {
            get
            {
                lock (_lock)
                {
                    return _last;
                }
            }
        }

        public void Push(in VirtualPadState state)
        {
            lock (_lock)
            {
                _last = state;
            }

            Interlocked.Increment(ref _count);
        }
    }
}
