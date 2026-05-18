using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Pipeline;

public class BindingPipelineSingleTests
{
    private static readonly KeyId W = KeyId.FromScanCode(0x11);

    [Fact]
    public void Trigger_binding_ramps_from_zero_to_full_over_press_ms()
    {
        var bindings = new[]
        {
            new SingleKeyBinding(W, BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f),
        };
        var pipeline = new BindingPipeline(bindings, Array.Empty<AxisPairBinding>());
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);

        store.Set(W, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, dtMs: 60f, ref pad);
        pad.RightTrigger.Should().BeApproximately(0.5f, 1e-4f);

        pipeline.Tick(store, dtMs: 60f, ref pad);
        pad.RightTrigger.Should().BeApproximately(1f, 1e-4f);
    }

    [Fact]
    public void Trigger_binding_uses_measured_value_when_provenance_is_analog()
    {
        var bindings = new[]
        {
            new SingleKeyBinding(W, BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f),
        };
        var pipeline = new BindingPipeline(bindings, Array.Empty<AxisPairBinding>());
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);

        store.Set(W, 0.42f, KeyProvenance.Analog);
        pipeline.Tick(store, dtMs: 1f, ref pad);
        pad.RightTrigger.Should().BeApproximately(0.42f, 1e-4f);
    }

    [Fact]
    public void Button_target_is_pressed_above_half()
    {
        var bindings = new[]
        {
            new SingleKeyBinding(W, BindingTarget.ButtonA, LinearCurve.Instance, 0f, 0f),
        };
        var pipeline = new BindingPipeline(bindings, Array.Empty<AxisPairBinding>());
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);

        store.Set(W, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);
        pad.ButtonA.Should().BeTrue();

        store.Set(W, 0f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);
        pad.ButtonA.Should().BeFalse();
    }
}
