using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;

namespace ApexMapper.Core.Tests.Pipeline;

public sealed class AnalogReturnTests
{
    private static readonly KeyId A = KeyId.FromScanCode(0x1E);
    private static readonly KeyId D = KeyId.FromScanCode(0x20);

    [Fact]
    public void Analog_press_and_repress_are_immediate_while_release_uses_elapsed_time()
    {
        var pipeline = new BindingPipeline(new[]
        {
            new SingleKeyBinding(A, BindingTarget.RightTrigger, LinearCurve.Instance, 500, 100),
        }, Array.Empty<AxisPairBinding>());
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);
        void Sample(float depth, float dt)
        {
            store.Set(A, depth, KeyProvenance.Analog);
            pipeline.Tick(store, dt, ref pad);
        }

        Sample(0.4f, 1);
        Assert.Equal(0.4f, pad.RightTrigger);
        Sample(0.8f, 1);
        Assert.Equal(0.8f, pad.RightTrigger);
        Sample(0.2f, 10);
        Assert.Equal(0.7f, pad.RightTrigger, 5);
        Sample(0.3f, 1);
        Assert.Equal(0.3f, pad.RightTrigger);
        Sample(0, 10);
        Assert.Equal(0.2f, pad.RightTrigger, 5);
        Sample(0, 25);
        Assert.Equal(0f, pad.RightTrigger);
    }

    [Fact]
    public void Axis_return_follows_socd_without_blocking_opposite_press()
    {
        var pipeline = Axis(100);
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);
        store.Set(A, 0.8f, KeyProvenance.Analog);
        store.Set(D, 0, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        Assert.Equal(-0.8f, pad.LeftStickX);
        store.Set(A, 0, KeyProvenance.Analog);
        pipeline.Tick(store, 10, ref pad);
        Assert.Equal(-0.7f, pad.LeftStickX, 5);
        store.Set(D, 0.2f, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        Assert.Equal(0.2f, pad.LeftStickX);
        store.Set(A, 0.2f, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        Assert.Equal(0f, pad.LeftStickX);
        store.Set(A, 0, KeyProvenance.Analog);
        store.Set(D, 0, KeyProvenance.Analog);
        pipeline.Tick(store, 25, ref pad);
        Assert.Equal(0f, pad.LeftStickX);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gate_or_provenance_reset_discards_axis_return(bool gate)
    {
        var pipeline = Axis(100);
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);
        store.Set(A, 0.8f, KeyProvenance.Analog);
        store.Set(D, 0, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        if (gate) store.GateHeldKeys();
        else store.Set(A, 0, KeyProvenance.Digital);
        pipeline.Tick(store, 1, ref pad);
        Assert.Equal(0f, pad.LeftStickX);
        store.Set(A, 0, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        Assert.Equal(0f, pad.LeftStickX);
    }

    [Fact]
    public void Zero_release_and_button_bindings_keep_immediate_analog_release()
    {
        var pipeline = new BindingPipeline(new[]
        {
            new SingleKeyBinding(A, BindingTarget.ButtonA, LinearCurve.Instance, 500, 100),
        }, new[] { new AxisPairBinding(A, D, BindingTarget.LeftStickX, LinearCurve.Instance, 500, 0, SocdMode.Neutral) });
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);
        store.Set(A, 0.8f, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        Assert.True(pad.ButtonA);
        store.Set(A, 0, KeyProvenance.Analog);
        pipeline.Tick(store, 1, ref pad);
        Assert.Equal(0f, pad.LeftStickX);
        Assert.False(pad.ButtonA);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_or_reset_cancels_return_after_sensor_is_already_zero(bool indexed)
    {
        var store = indexed ? new KeyStateStore(new KeyIndex(new[] { A, D })) : new KeyStateStore();
        var pipeline = Axis(100);
        var pad = default(VirtualPadState);
        Action[] transitions = { store.GateHeldKeys, () => store.GateHeldKeys(KeyProvenance.Analog), store.Reset };
        foreach (var transition in transitions)
        {
            store.Set(A, 0.8f, KeyProvenance.Analog);
            store.Set(D, 0, KeyProvenance.Analog);
            pipeline.Tick(store, 1, ref pad);
            store.Set(A, 0, KeyProvenance.Analog);
            pipeline.Tick(store, 10, ref pad);
            Assert.Equal(-0.7f, pad.LeftStickX, 5);
            Assert.False(store.IsGated(A));

            transition();
            pipeline.Tick(store, 1, ref pad);

            Assert.Equal(0f, pad.LeftStickX);
        }
    }

    private static BindingPipeline Axis(float releaseMs) => new(Array.Empty<SingleKeyBinding>(), new[]
    {
        new AxisPairBinding(A, D, BindingTarget.LeftStickX, LinearCurve.Instance, 500, releaseMs, SocdMode.Neutral),
    });
}
