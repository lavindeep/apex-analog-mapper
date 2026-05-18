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

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) pipeline.Tick(store, 1f, ref pad);
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0, "BindingPipeline.Tick must not allocate after warm-up");
    }
}
