using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Pipeline;

public class BindingPipelineAllocationTests
{
    [Fact]
    public void Steady_state_tick_does_not_allocate()
    {
        var singles = new[]
        {
            new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f),
        };
        var axes = new[]
        {
            new AxisPairBinding(KeyId.FromScanCode(0x1E), KeyId.FromScanCode(0x20), BindingTarget.LeftStickX, LinearCurve.Instance, 80f, 80f, SocdMode.Neutral),
        };
        var pipeline = new BindingPipeline(singles, axes);
        var store = new KeyStateStore();
        store.Set(KeyId.FromScanCode(0x11), 1f, KeyProvenance.Digital);
        store.Set(KeyId.FromScanCode(0x20), 1f, KeyProvenance.Digital);

        var pad = default(VirtualPadState);
        for (var i = 0; i < 1000; i++) pipeline.Tick(store, 1f, ref pad);

        // Assert on the minimum of several windows: a genuine per-call
        // allocation shows up in every window, while a one-off runtime-service
        // allocation (tiered-JIT promotion, eventing) lands in at most one and
        // must not flake the gate on shared CI runners.
        var windows = new long[3];
        for (var w = 0; w < windows.Length; w++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) pipeline.Tick(store, 1f, ref pad);
            windows[w] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        windows.Min().Should().Be(0, "BindingPipeline.Tick must not allocate after warm-up in any clean window");
    }

    [Fact]
    public void Steady_state_tick_does_not_allocate_with_a_shaped_curve_and_stronger_analog_socd()
    {
        var shaped = new DeadzoneCurve(
            new PiecewiseCubicCurve(new[] { (0f, 0f), (0.5f, 0.3f), (1f, 1f) }),
            innerDeadzone: 0.1f,
            outerDeadzone: 0.9f);
        var singles = new[]
        {
            new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, shaped, 120f, 0f),
        };
        var axes = new[]
        {
            new AxisPairBinding(KeyId.FromScanCode(0x1E), KeyId.FromScanCode(0x20), BindingTarget.LeftStickX, shaped, 80f, 80f, SocdMode.StrongerAnalogWins),
        };
        var pipeline = new BindingPipeline(singles, axes);
        var store = new KeyStateStore();
        store.Set(KeyId.FromScanCode(0x11), 1f, KeyProvenance.Digital);
        // Both axis sides active so the stronger-analog hysteresis path runs every tick.
        store.Set(KeyId.FromScanCode(0x1E), 0.6f, KeyProvenance.Analog);
        store.Set(KeyId.FromScanCode(0x20), 0.8f, KeyProvenance.Analog);

        var pad = default(VirtualPadState);
        for (var i = 0; i < 1000; i++) pipeline.Tick(store, 1f, ref pad);

        // Assert on the minimum of several windows: a genuine per-call
        // allocation shows up in every window, while a one-off runtime-service
        // allocation (tiered-JIT promotion, eventing) lands in at most one and
        // must not flake the gate on shared CI runners.
        var windows = new long[3];
        for (var w = 0; w < windows.Length; w++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) pipeline.Tick(store, 1f, ref pad);
            windows[w] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        windows.Min().Should().Be(0, "shaped-curve + stronger-analog SOCD tick must not allocate after warm-up in any clean window");
    }
}
