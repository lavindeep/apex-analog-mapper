using ApexMapper.Core.Curves;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Curves;

public class LinearCurveTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    public void Maps_identity(float input, float expected)
    {
        new LinearCurve().Map(input).Should().BeApproximately(expected, 1e-6f);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    public void Clamps_to_unit_interval(float input, float expected)
    {
        new LinearCurve().Map(input).Should().Be(expected);
    }
}
