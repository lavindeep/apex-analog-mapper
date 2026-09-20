namespace ApexMapper.Core.Calibration;

/// <summary>Raw sensor counts to depth in 0..1.</summary>
public static class Normalizer
{
    /// <summary>
    /// Re-scales above the noise band so the first reading outside the band is a hair
    /// above zero rather than a step: depth = (|raw - rest| - band) / (span - band).
    /// Readings inside the band, or on the wrong side of rest, are exactly zero.
    /// </summary>
    public static float Depth(KeyCalibration cal, int raw)
    {
        var delta = cal.TravelsUpward ? raw - cal.Rest : cal.Rest - raw;
        if (delta <= cal.NoiseBand)
        {
            return 0f;
        }
        var depth = (delta - cal.NoiseBand) / (float)(cal.Span - cal.NoiseBand);
        return depth >= 1f ? 1f : depth;
    }

    public static bool IsAtRest(KeyCalibration cal, int raw) => Depth(cal, raw) == 0f;
}
