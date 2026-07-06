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
}
