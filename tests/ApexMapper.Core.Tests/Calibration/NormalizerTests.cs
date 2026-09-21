using ApexMapper.Core.Calibration;
using Xunit;

namespace ApexMapper.Core.Tests.Calibration;

public class NormalizerTests
{
    // W on the maintainer's board: rest 878, full 4095, band 20.
    private static readonly KeyCalibration W = KeyCalibration.Create(878, 4095, 20, 16);

    [Fact]
    public void Inside_the_band_is_exactly_zero_and_at_rest()
    {
        Assert.Equal(0f, Normalizer.Depth(W, 878));
        Assert.Equal(0f, Normalizer.Depth(W, 898));
        Assert.Equal(0f, Normalizer.Depth(W, 860));
        Assert.True(Normalizer.IsAtRest(W, 898));
    }

    [Fact]
    public void Leaving_the_band_has_no_step()
    {
        var justOutside = Normalizer.Depth(W, 899);
        Assert.Equal(1f / 3197f, justOutside, 0.000001f);
        Assert.False(Normalizer.IsAtRest(W, 899));
    }

    [Fact]
    public void Wrong_side_of_rest_and_beyond_full_press_clamp_on_an_ascending_key()
    {
        Assert.Equal(0f, Normalizer.Depth(W, 500));
        Assert.Equal(0f, Normalizer.Depth(W, int.MinValue));
        var shortKey = KeyCalibration.Create(878, 3500, 20, 0);
        Assert.Equal(1f, Normalizer.Depth(shortKey, 3501));
        Assert.Equal(1f, Normalizer.Depth(shortKey, 4095));
        Assert.Equal(1f, Normalizer.Depth(shortKey, int.MaxValue));
    }

    [Fact]
    public void An_unvalidated_calibration_with_no_usable_span_reads_zero()
    {
        Assert.Equal(0f, Normalizer.Depth(new KeyCalibration(800, 820, 20, 0), 821));
        Assert.Equal(0f, Normalizer.Depth(new KeyCalibration(800, 800, 0, 0), 799));
    }

    [Fact]
    public void The_test_inverse_agrees_with_the_normaliser()
    {
        Assert.Equal(0.5f, Normalizer.Depth(Engine.Fixtures.W, Engine.Fixtures.CountFor(Engine.Fixtures.W, 0.5f)), 0.001f);
    }

    [Fact]
    public void A_key_that_reaches_the_ceiling_is_clipping()
    {
        Assert.True(W.IsClipping);
        Assert.False(Engine.Fixtures.A.IsClipping);
        Assert.True(KeyCalibration.Create(3000, 0, 20, 0).IsClipping);
        Assert.False(KeyCalibration.Create(3000, 800, 20, 0).IsClipping);
    }

    [Fact]
    public void Rest_drift_within_the_band_still_reads_at_rest()
    {
        // The rest reading drifted up by 15 counts since calibration.
        Assert.True(Normalizer.IsAtRest(W, 878 + 15));
    }

    [Fact]
    public void Full_press_and_beyond_clamp_to_one()
    {
        Assert.Equal(1f, Normalizer.Depth(W, 4095));
        var half = Normalizer.Depth(W, 878 + 20 + (4095 - 878 - 20) / 2);
        Assert.InRange(half, 0.499f, 0.501f);
    }

    [Fact]
    public void Descending_travel_normalises_the_same_way()
    {
        var descending = KeyCalibration.Create(3000, 800, 20, 0);
        Assert.Equal(0f, Normalizer.Depth(descending, 3000));
        Assert.Equal(0f, Normalizer.Depth(descending, 3010));
        Assert.Equal(1f, Normalizer.Depth(descending, 800));
        Assert.True(Normalizer.Depth(descending, 2900) > 0f);
        Assert.Equal(0f, Normalizer.Depth(descending, 3500));
    }

    [Fact]
    public void Calibration_rejects_a_span_too_small_for_the_band()
    {
        Assert.NotNull(KeyCalibration.Validate(878, 990, 20, 0));
        Assert.Null(KeyCalibration.Validate(878, 998, 20, 0));
        Assert.NotNull(KeyCalibration.Validate(878, 4096, 20, 0));
        Assert.NotNull(KeyCalibration.Validate(878, 4095, 20, 70));
        Assert.NotNull(KeyCalibration.Validate(878, 4095, -1, 0));
        Assert.Throws<ArgumentException>(() => KeyCalibration.Create(878, 900, 20, 0));
    }

    [Theory]
    [InlineData(5, 20)]
    [InlineData(14, 21)]
    [InlineData(21, 32)]
    public void Noise_band_is_the_default_or_one_and_a_half_times_measured_noise(int peakToPeak, int expected)
    {
        Assert.Equal(expected, KeyCalibration.NoiseBandFor(peakToPeak));
    }
}
