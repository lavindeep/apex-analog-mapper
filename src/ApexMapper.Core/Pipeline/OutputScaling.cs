namespace ApexMapper.Core.Pipeline;

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
