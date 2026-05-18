using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Pipeline;

public class BindingPipelineAxisTests
{
    private static readonly KeyId A = KeyId.FromScanCode(0x1E);
    private static readonly KeyId D = KeyId.FromScanCode(0x20);

    [Fact]
    public void Single_side_press_drives_signed_axis_to_full()
    {
        var pipeline = new BindingPipeline(
            Array.Empty<SingleKeyBinding>(),
            new[] { new AxisPairBinding(A, D, BindingTarget.LeftStickX, LinearCurve.Instance, 0f, 0f, SocdMode.Neutral) });
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);

        store.Set(A, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);
        pad.LeftStickX.Should().BeApproximately(-1f, 1e-4f);

        store.Set(A, 0f, KeyProvenance.Digital);
        store.Set(D, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);
        pad.LeftStickX.Should().BeApproximately(1f, 1e-4f);
    }

    [Fact]
    public void Both_pressed_neutral_yields_zero()
    {
        var pipeline = new BindingPipeline(
            Array.Empty<SingleKeyBinding>(),
            new[] { new AxisPairBinding(A, D, BindingTarget.LeftStickX, LinearCurve.Instance, 0f, 0f, SocdMode.Neutral) });
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);

        store.Set(A, 1f, KeyProvenance.Digital);
        store.Set(D, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);
        pad.LeftStickX.Should().Be(0f);
    }

    [Fact]
    public void Last_input_wins_axis()
    {
        var pipeline = new BindingPipeline(
            Array.Empty<SingleKeyBinding>(),
            new[] { new AxisPairBinding(A, D, BindingTarget.LeftStickX, LinearCurve.Instance, 0f, 0f, SocdMode.LastInputWins) });
        var store = new KeyStateStore();
        var pad = default(VirtualPadState);

        store.Set(A, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);

        store.Set(D, 1f, KeyProvenance.Digital);
        pipeline.Tick(store, 16f, ref pad);
        pad.LeftStickX.Should().BeApproximately(1f, 1e-4f);
    }
}
