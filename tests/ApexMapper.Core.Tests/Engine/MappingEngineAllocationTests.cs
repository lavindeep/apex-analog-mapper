using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Engine;

public class MappingEngineAllocationTests
{
    private static readonly KeyId Throttle = KeyId.FromScanCode(0x11);
    private static readonly KeyId SteerLeft = KeyId.FromScanCode(0x1E);
    private static readonly KeyId SteerRight = KeyId.FromScanCode(0x20);

    private sealed class CountingSink : IPadStateSink
    {
        private VirtualPadState _last;
        private long _count;

        public long Count => _count;

        public VirtualPadState Last => _last;

        public void Push(in VirtualPadState state)
        {
            _last = state;
            _count++;
        }
    }

    [Fact]
    public void Steady_state_ticks_do_not_allocate()
    {
        var profile = new Profile(
            "alloc",
            "alloc",
            new DeviceMatcher(0x1038, 0x161C, null, null),
            new GameMatcher(null, null, null),
            ActivationPolicy.Default,
            new[] { new SingleKeyBinding(Throttle, BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f) },
            new[] { new AxisPairBinding(SteerLeft, SteerRight, BindingTarget.LeftStickX, LinearCurve.Instance, 80f, 80f, SocdMode.Neutral) },
            Notes: null);

        var store = new KeyStateStore();
        var sink = new CountingSink();
        var engine = new MappingEngine(store, sink);
        engine.SetProfile(profile);
        store.Set(Throttle, 0.7f, KeyProvenance.Analog);
        store.Set(SteerLeft, 0.4f, KeyProvenance.Analog);
        // Digital provenance so the ramp path runs every tick too.
        store.Set(SteerRight, 1f, KeyProvenance.Digital);

        for (var i = 0; i < 1_000; i++)
        {
            engine.TickOnce(1f);
        }

        // Assert on the minimum of several windows: a genuine per-tick
        // allocation shows up in every window, while a one-off runtime-service
        // allocation (tiered-JIT promotion, eventing) lands in at most one and
        // must not flake the gate on shared CI runners.
        var windows = new long[3];
        for (var w = 0; w < windows.Length; w++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
            {
                engine.TickOnce(1f);
            }

            windows[w] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        windows.Min().Should().Be(0, "MappingEngine.TickOnce must not allocate after warm-up in any clean window");
    }
}
