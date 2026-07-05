using ApexMapper.Input.Abstractions.Calibration;

namespace ApexMapper.Input.Abstractions.Tests.Calibration;

public class CalibrationCurveTests
{
    [Fact]
    public void Linear_midpoint_normalizes_to_about_one_half()
    {
        var curve = new CalibrationCurve(Rest: 0f, Max: 255f, NoiseBand: 2f, Kind: NormalizationKind.Linear);
        curve.Normalize(128f).Should().BeApproximately(128f / 255f, 1e-4f);
    }

    [Fact]
    public void Linear_at_rest_returns_zero()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);
        curve.Normalize(0f).Should().Be(0f);
    }

    [Fact]
    public void Linear_at_max_returns_one()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);
        curve.Normalize(255f).Should().Be(1f);
    }

    [Fact]
    public void Linear_inside_noise_band_returns_zero()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);
        curve.Normalize(1f).Should().Be(0f);
    }

    [Fact]
    public void Linear_above_max_clamps_to_one()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);
        curve.Normalize(300f).Should().Be(1f);
    }

    [Fact]
    public void Linear_below_rest_clamps_to_zero()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);
        curve.Normalize(-10f).Should().Be(0f);
    }

    // Inverted travel is authored the same ascending way as Linear (raw bounds
    // in RawMin..RawMax order); the Kind flag alone reverses the mapping, so the
    // physical rest is at the Max endpoint and full press at the Rest endpoint.
    [Fact]
    public void Inverted_at_physical_rest_high_raw_returns_zero()
    {
        var curve = new CalibrationCurve(Rest: 0f, Max: 255f, NoiseBand: 2f, Kind: NormalizationKind.Inverted);
        curve.Normalize(255f).Should().Be(0f);
    }

    [Fact]
    public void Inverted_at_full_press_low_raw_returns_one()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Inverted);
        curve.Normalize(0f).Should().Be(1f);
    }

    [Fact]
    public void Inverted_midpoint_returns_about_one_half()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Inverted);
        curve.Normalize(127f).Should().BeApproximately(128f / 255f, 1e-3f);
    }

    [Fact]
    public void Inverted_inside_noise_band_of_high_rest_returns_zero()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Inverted);
        curve.Normalize(254f).Should().Be(0f);
    }

    [Fact]
    public void Inverted_below_rest_endpoint_clamps_to_one()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Inverted);
        curve.Normalize(-10f).Should().Be(1f);
    }

    [Fact]
    public void Inverted_above_max_endpoint_clamps_to_zero()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Inverted);
        curve.Normalize(300f).Should().Be(0f);
    }

    [Fact]
    public void Linear_and_inverted_are_mirror_images_across_the_range()
    {
        var linear = new CalibrationCurve(0f, 255f, 0f, NormalizationKind.Linear);
        var inverted = new CalibrationCurve(0f, 255f, 0f, NormalizationKind.Inverted);

        foreach (var raw in new[] { 0f, 64f, 128f, 192f, 255f })
        {
            (linear.Normalize(raw) + inverted.Normalize(raw)).Should().BeApproximately(1f, 1e-4f);
        }
    }

    [Fact]
    public void Degenerate_rest_equals_max_returns_zero_for_any_input()
    {
        var curve = new CalibrationCurve(100f, 100f, 0f, NormalizationKind.Linear);
        curve.Normalize(0f).Should().Be(0f);
        curve.Normalize(100f).Should().Be(0f);
        curve.Normalize(500f).Should().Be(0f);
        curve.Normalize(-50f).Should().Be(0f);
    }

    [Fact]
    public void Noise_band_wider_than_range_zeroes_mid_input()
    {
        var curve = new CalibrationCurve(Rest: 128f, Max: 255f, NoiseBand: 200f, Kind: NormalizationKind.Linear);
        curve.Normalize(150f).Should().Be(0f);
    }

    [Fact]
    public void Normalize_does_not_allocate()
    {
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);

        // Warm up JIT
        for (var i = 0; i < 1000; i++)
        {
            _ = curve.Normalize(128f);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            _ = curve.Normalize(128f);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0);
    }
}
