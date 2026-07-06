using ApexMapper.Core.Pipeline;

namespace ApexMapper.Output.ViGEm;

/// <summary>
/// Converts a <see cref="VirtualPadState"/> into the driver's numeric units.
/// The axis and trigger conversions are fail-closed: a non-finite value (NaN or
/// ±Infinity, which no healthy pipeline produces) collapses that channel to its
/// neutral resting value rather than emitting garbage to the pad; finite values
/// outside the valid range clamp to the range. Buttons pass through unchanged.
/// </summary>
public static class Xbox360ReportPacker
{
    public static Xbox360Report Pack(in VirtualPadState state) => new()
    {
        LeftStickX = PackStick(state.LeftStickX),
        LeftStickY = PackStick(state.LeftStickY),
        RightStickX = PackStick(state.RightStickX),
        RightStickY = PackStick(state.RightStickY),
        LeftTrigger = PackTrigger(state.LeftTrigger),
        RightTrigger = PackTrigger(state.RightTrigger),

        A = state.ButtonA,
        B = state.ButtonB,
        X = state.ButtonX,
        Y = state.ButtonY,
        LeftShoulder = state.ButtonLB,
        RightShoulder = state.ButtonRB,
        Start = state.ButtonStart,
        Back = state.ButtonBack,
        LeftThumb = state.ButtonLS,
        RightThumb = state.ButtonRS,
        Guide = state.ButtonGuide,
        DpadUp = state.DpadUp,
        DpadDown = state.DpadDown,
        DpadLeft = state.DpadLeft,
        DpadRight = state.DpadRight,
    };

    // Full symmetric ±32767 range. The negative extreme floors at -32767 rather
    // than short.MinValue (-32768) so full-left and full-right are equal
    // magnitudes: a downstream curve or deadzone never sees a lopsided axis. The
    // discarded LSB at the extreme is below any human or game's resolution.
    private static short PackStick(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        var scaled = value * 32767f;
        if (scaled >= 32767f)
        {
            return 32767;
        }

        if (scaled <= -32767f)
        {
            return -32767;
        }

        return (short)MathF.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private static byte PackTrigger(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        var scaled = value * 255f;
        if (scaled >= 255f)
        {
            return 255;
        }

        if (scaled <= 0f)
        {
            return 0;
        }

        return (byte)MathF.Round(scaled, MidpointRounding.AwayFromZero);
    }
}
