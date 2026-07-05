namespace ApexMapper.Core.Pipeline;

/// <summary>
/// Scales normalized pipeline values to Xbox controller ranges.
/// </summary>
/// <remarks>
/// Sticks are mapped symmetrically to ±32767 by design. The Xbox thumbstick range is technically
/// asymmetric (−32768..32767), but a synthetic axis driven from keys benefits from a symmetric
/// centre so equal-magnitude left/right (or up/down) inputs produce equal-magnitude output; the
/// unreachable −32768 endpoint is not worth the asymmetry. This intentionally amends the spec's
/// full-range wording.
/// </remarks>
public static class OutputScaling
{
    public static byte ToTrigger(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 1f) return 255;
        return (byte)MathF.Round(value * 255f);
    }

    public static short ToStick(float value)
    {
        if (value >= 1f) return 32767;
        if (value <= -1f) return -32767;
        return (short)MathF.Round(value * 32767f);
    }
}
