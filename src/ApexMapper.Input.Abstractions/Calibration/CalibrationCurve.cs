namespace ApexMapper.Input.Abstractions.Calibration;

public enum NormalizationKind
{
    Linear,
    Inverted,
}

public readonly record struct CalibrationCurve(
    float Rest,
    float Max,
    float NoiseBand,
    NormalizationKind Kind)
{
    public float Normalize(float raw)
    {
        var span = Max - Rest;
        if (MathF.Abs(span) < 1e-7f)
        {
            return 0f;
        }

        if (MathF.Abs(raw - Rest) < NoiseBand)
        {
            return 0f;
        }

        var v = (raw - Rest) / span;
        return Math.Clamp(v, 0f, 1f);
    }
}
