namespace ApexMapper.Core.Calibration;

/// <summary>
/// Per-key calibration in raw 12-bit sensor counts. Travel may run upward or downward
/// from rest; the sign of <c>FullPress - Rest</c> decides. A key that reaches the 4095
/// ceiling before bottoming out stores 4095.
/// </summary>
/// <param name="Rest">Reading with the key released.</param>
/// <param name="FullPress">Reading with the key fully pressed.</param>
/// <param name="NoiseBand">Counts around rest treated as released.</param>
/// <param name="SensorIndex">Flat sensor index 0..69 (group - 1) * 14 + slot.</param>
public sealed record KeyCalibration(int Rest, int FullPress, int NoiseBand, int SensorIndex)
{
    public const int MaxCount = 4095;
    /// <summary>Floor for the noise band: twice the worst ten-minute rest noise of a bound key on the measured board (D, 34 counts raw), still under the 54 to 80 counts at which the keyboard's own digital key-down fires.</summary>
    public const int DefaultNoiseBand = 40;
    public const int MinimumSpanAboveBand = 100;

    public int Span => Math.Abs(FullPress - Rest);

    public bool TravelsUpward => FullPress > Rest;

    /// <summary>The key reaches the sensor's ceiling before bottoming out, so the last part of its travel produces no change.</summary>
    public bool IsClipping => TravelsUpward ? FullPress >= MaxCount : FullPress <= 0;

    public static string? Validate(int rest, int fullPress, int noiseBand, int sensorIndex)
    {
        if (rest is < 0 or > MaxCount || fullPress is < 0 or > MaxCount)
        {
            return "Rest and full-press readings must be within 0..4095.";
        }
        if (noiseBand < 0)
        {
            return "Noise band cannot be negative.";
        }
        if (Math.Abs(fullPress - rest) < noiseBand + MinimumSpanAboveBand)
        {
            return $"Full press must differ from rest by at least {noiseBand + MinimumSpanAboveBand} counts.";
        }
        if (sensorIndex is < 0 or >= 70)
        {
            return "Sensor index must be 0..69.";
        }
        return null;
    }

    public static KeyCalibration Create(int rest, int fullPress, int noiseBand, int sensorIndex)
    {
        var error = Validate(rest, fullPress, noiseBand, sensorIndex);
        return error is null ? new KeyCalibration(rest, fullPress, noiseBand, sensorIndex) : throw new ArgumentException(error);
    }

    /// <summary>The band to store: the default, or 1.5 times the measured rest noise if larger.</summary>
    public static int NoiseBandFor(int measuredPeakToPeak) =>
        Math.Max(DefaultNoiseBand, (int)Math.Ceiling(measuredPeakToPeak * 1.5));
}
