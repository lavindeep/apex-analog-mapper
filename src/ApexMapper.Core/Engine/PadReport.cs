using ApexMapper.Core.Bindings;

namespace ApexMapper.Core.Engine;

/// <summary>
/// The packed Xbox 360 report the driver receives. Value equality lets the engine
/// skip submits when nothing the pad can express has changed.
/// </summary>
public record struct PadReport(
    short LeftStickX,
    short LeftStickY,
    short RightStickX,
    short RightStickY,
    byte LeftTrigger,
    byte RightTrigger,
    ushort Buttons)
{
    public const short StickMax = 32767;

    public static readonly PadReport Neutral = default;

    /// <summary>
    /// Symmetric packing: full left and full right are both 32767 in magnitude. The
    /// unreachable -32768 would make one direction a hair stronger than the other.
    /// Non-finite input packs to neutral.
    /// </summary>
    public static short PackStick(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }
        var scaled = value * StickMax;
        if (scaled >= StickMax)
        {
            return StickMax;
        }
        if (scaled <= -StickMax)
        {
            return -StickMax;
        }
        return (short)MathF.Round(scaled, MidpointRounding.AwayFromZero);
    }

    public static byte PackTrigger(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 0;
        }
        var scaled = value * 255f;
        return scaled >= 255f ? (byte)255 : (byte)MathF.Round(scaled, MidpointRounding.AwayFromZero);
    }

    public void SetAxis(PadTarget target, float value)
    {
        var packed = PackStick(value);
        switch (target)
        {
            case PadTarget.LeftStickX: LeftStickX = packed; break;
            case PadTarget.LeftStickY: LeftStickY = packed; break;
            case PadTarget.RightStickX: RightStickX = packed; break;
            case PadTarget.RightStickY: RightStickY = packed; break;
            default: throw new ArgumentException($"{target} is not an axis.", nameof(target));
        }
    }

    public void SetTrigger(PadTarget target, float value)
    {
        var packed = PackTrigger(value);
        switch (target)
        {
            case PadTarget.LeftTrigger: LeftTrigger = packed; break;
            case PadTarget.RightTrigger: RightTrigger = packed; break;
            default: throw new ArgumentException($"{target} is not a trigger.", nameof(target));
        }
    }

    public void SetButton(PadTarget target, bool pressed)
    {
        var bit = target.ButtonBit();
        Buttons = pressed ? (ushort)(Buttons | bit) : (ushort)(Buttons & ~bit);
    }
}
