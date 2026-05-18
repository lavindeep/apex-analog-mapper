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
}
