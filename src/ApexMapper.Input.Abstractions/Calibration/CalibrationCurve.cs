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
        // Inverted-travel switches read high-at-rest and fall toward full press,
        // so the Max endpoint is the physical rest and Rest is full press. Swap
        // the reference ends for that kind; otherwise the enum is a silent no-op
        // and an inverted device reads backwards.
        var (rest, max) = Kind == NormalizationKind.Inverted ? (Max, Rest) : (Rest, Max);

        var span = max - rest;
        if (MathF.Abs(span) < 1e-7f)
        {
            return 0f;
        }

        if (MathF.Abs(raw - rest) < NoiseBand)
        {
            return 0f;
        }

        var v = (raw - rest) / span;
        return Math.Clamp(v, 0f, 1f);
    }
}
