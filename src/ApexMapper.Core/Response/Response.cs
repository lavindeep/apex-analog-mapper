namespace ApexMapper.Core.Response;

/// <summary>
/// Shapes a depth in 0..1 into an output in 0..1. Three floats cover every curve the
/// app needs and edit well as sliders: a deadzone below which output is zero, a
/// saturation depth at which output reaches full, and an exponent applied between
/// them (1 is linear, above 1 is gentle at the start, below 1 is eager).
/// </summary>
public sealed record Response(float Exponent, float Saturation, float Deadzone)
{
    public const float MinExponent = 0.25f;
    public const float MaxExponent = 4f;

    /// <summary>
    /// The one curve the app ships. A key's reading barely changes over the first part of
    /// the press, so the most eager curve is what makes the output follow the finger: about
    /// half the output a fifth of the way down and three quarters at halfway. Chosen by
    /// driving Forza Horizon 6 on an Apex Pro TKL (issue #9).
    /// </summary>
    public static readonly Response Default = new(MinExponent, 1f, 0f);

    public static string? Validate(float exponent, float saturation, float deadzone)
    {
        if (!float.IsFinite(exponent) || exponent is < MinExponent or > MaxExponent)
        {
            return $"Exponent must be between {MinExponent} and {MaxExponent}.";
        }
        if (!float.IsFinite(deadzone) || !float.IsFinite(saturation) || deadzone < 0f || saturation > 1f || deadzone >= saturation)
        {
            return "Deadzone must be at least 0 and below the saturation point, which is at most 1.";
        }
        return null;
    }

    public static Response Create(float exponent, float saturation, float deadzone)
    {
        var error = Validate(exponent, saturation, deadzone);
        return error is null ? new Response(exponent, saturation, deadzone) : throw new ArgumentException(error);
    }

    public float Map(float depth)
    {
        if (!(depth > Deadzone))
        {
            return 0f;
        }
        if (depth >= Saturation)
        {
            return 1f;
        }
        var scaled = (depth - Deadzone) / (Saturation - Deadzone);
        return Exponent == 1f ? scaled : MathF.Pow(scaled, Exponent);
    }
}
