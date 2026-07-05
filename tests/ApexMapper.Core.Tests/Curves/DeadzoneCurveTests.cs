using ApexMapper.Core.Curves;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Curves;

public class DeadzoneCurveTests
{
    [Fact]
    public void Inner_deadzone_outputs_zero()
    {
        var curve = new DeadzoneCurve(LinearCurve.Instance, innerDeadzone: 0.1f, outerDeadzone: 0.9f);
        curve.Map(0f).Should().Be(0f);
        curve.Map(0.05f).Should().Be(0f);
        curve.Map(0.1f).Should().Be(0f);
    }

    [Fact]
    public void Outer_deadzone_outputs_one()
    {
        var curve = new DeadzoneCurve(LinearCurve.Instance, innerDeadzone: 0.1f, outerDeadzone: 0.9f);
        curve.Map(0.9f).Should().Be(1f);
        curve.Map(0.95f).Should().Be(1f);
        curve.Map(1f).Should().Be(1f);
    }

    [Fact]
    public void Middle_rescales_via_inner_curve()
    {
        var curve = new DeadzoneCurve(LinearCurve.Instance, innerDeadzone: 0.2f, outerDeadzone: 0.8f);
        curve.Map(0.5f).Should().BeApproximately(0.5f, 1e-6f);
        curve.Map(0.2f + (0.8f - 0.2f) * 0.25f).Should().BeApproximately(0.25f, 1e-6f);
    }

    [Theory]
    [InlineData(-0.1f, 0.5f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.4f, 0.4f)]
    [InlineData(0.5f, 0.4f)]
    public void Rejects_invalid_deadzones(float inner, float outer)
    {
        Action a = () => _ = new DeadzoneCurve(LinearCurve.Instance, inner, outer);
        a.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_inner_curve_that_does_not_reach_one()
    {
        // An inner curve ending below 1 leaves a discontinuous jump at the outer edge
        // (0.6 -> 1.0). It must be rejected at construction.
        var endsAtPointSix = new ConstantCurve(0.6f);
        Action a = () => _ = new DeadzoneCurve(endsAtPointSix, innerDeadzone: 0f, outerDeadzone: 1f);
        a.Should().Throw<ArgumentException>();
    }

    private sealed class ConstantCurve : ICurve
    {
        private readonly float _value;
        public ConstantCurve(float value) => _value = value;
        public float Map(float input) => _value;
    }

    [Fact]
    public void Allows_an_anti_deadzone_offset_at_the_inner_edge()
    {
        // An inner curve starting above 0 is a deliberate anti-deadzone: past the inner edge the
        // output jumps straight to a minimum. Only the outer edge is checked for continuity, so
        // this offset is allowed, not rejected.
        var offset = new PiecewiseCubicCurve(new[] { (0f, 0.3f), (1f, 1f) });
        var curve = new DeadzoneCurve(offset, innerDeadzone: 0f, outerDeadzone: 1f);
        curve.Map(0f).Should().Be(0f);
        curve.Map(0.01f).Should().BeGreaterThan(0.29f);
    }

    [Fact]
    public void Compliant_curve_is_continuous_across_the_outer_boundary()
    {
        var curve = new DeadzoneCurve(LinearCurve.Instance, innerDeadzone: 0.1f, outerDeadzone: 0.9f);
        var justBelow = curve.Map(0.8999f);
        var atBoundary = curve.Map(0.9f);
        atBoundary.Should().Be(1f);
        (atBoundary - justBelow).Should().BeLessThan(1e-2f);
    }
}
