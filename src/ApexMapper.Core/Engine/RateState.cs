namespace ApexMapper.Core.Engine;

/// <summary>
/// Rate mode integration: the signed input sets how fast the stick moves toward full
/// deflection (full input covers centre to full in <c>rateMs</c>); zero input returns
/// the stick to centre over <c>returnMs</c>.
/// </summary>
public struct RateState
{
    public float Deflection { get; private set; }

    public void Reset() => Deflection = 0f;

    public float Step(float signed, float dtMs, float rateMs, float returnMs)
    {
        if (!float.IsFinite(dtMs) || dtMs <= 0f)
        {
            return Deflection;
        }
        if (signed != 0f)
        {
            Deflection = Math.Clamp(Deflection + signed * dtMs / rateMs, -1f, 1f);
            return Deflection;
        }
        if (returnMs <= 0f)
        {
            Deflection = 0f;
            return Deflection;
        }
        var step = dtMs / returnMs;
        Deflection = MathF.Abs(Deflection) <= step ? 0f : Deflection - MathF.Sign(Deflection) * step;
        return Deflection;
    }
}
