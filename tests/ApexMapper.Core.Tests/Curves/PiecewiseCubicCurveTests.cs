using ApexMapper.Core.Curves;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Curves;

public class PiecewiseCubicCurveTests
{
    [Fact]
    public void Two_points_matches_linear()
    {
        var curve = new PiecewiseCubicCurve(new[] { (0f, 0f), (1f, 1f) });
        curve.Map(0.25f).Should().BeApproximately(0.25f, 1e-4f);
        curve.Map(0.5f).Should().BeApproximately(0.5f, 1e-4f);
        curve.Map(0.75f).Should().BeApproximately(0.75f, 1e-4f);
    }

    [Fact]
    public void Passes_through_control_points()
    {
        var pts = new[] { (0f, 0f), (0.3f, 0.1f), (0.7f, 0.9f), (1f, 1f) };
        var curve = new PiecewiseCubicCurve(pts);
        foreach (var (x, y) in pts)
        {
            curve.Map(x).Should().BeApproximately(y, 1e-4f);
        }
    }

    [Fact]
    public void Is_monotonic_non_decreasing()
    {
        var curve = new PiecewiseCubicCurve(new[] { (0f, 0f), (0.4f, 0.05f), (0.6f, 0.95f), (1f, 1f) });
        var prev = -1f;
        for (var x = 0f; x <= 1f; x += 0.01f)
        {
            var y = curve.Map(x);
            y.Should().BeGreaterThanOrEqualTo(prev - 1e-4f);
            prev = y;
        }
    }

    [Fact]
    public void Clamps_outside_domain()
    {
        var curve = new PiecewiseCubicCurve(new[] { (0f, 0f), (1f, 1f) });
        curve.Map(-0.5f).Should().Be(0f);
        curve.Map(1.5f).Should().Be(1f);
    }

    [Fact]
    public void Requires_at_least_two_points_and_endpoints_at_zero_and_one()
    {
        Action a = () => _ = new PiecewiseCubicCurve(new[] { (0f, 0f) });
        a.Should().Throw<ArgumentException>();

        Action b = () => _ = new PiecewiseCubicCurve(new[] { (0.1f, 0f), (1f, 1f) });
        b.Should().Throw<ArgumentException>();
    }
}
