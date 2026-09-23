namespace ApexMapper.Core.Bindings;

/// <summary>Every control on an Xbox 360 pad.</summary>
public enum PadTarget
{
    ButtonA,
    ButtonB,
    ButtonX,
    ButtonY,
    LeftBumper,
    RightBumper,
    Start,
    Back,
    LeftStickClick,
    RightStickClick,
    Guide,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
    LeftTrigger,
    RightTrigger,
    LeftStickX,
    LeftStickY,
    RightStickX,
    RightStickY,
}

public static class PadTargetExtensions
{
    public static bool IsAxis(this PadTarget target) => target is PadTarget.LeftStickX or PadTarget.LeftStickY or PadTarget.RightStickX or PadTarget.RightStickY;

    public static bool IsTrigger(this PadTarget target) => target is PadTarget.LeftTrigger or PadTarget.RightTrigger;

    public static bool IsButton(this PadTarget target) => !target.IsAxis() && !target.IsTrigger();

    /// <summary>XInput wButtons bit for a button target.</summary>
    public static ushort ButtonBit(this PadTarget target) => target switch
    {
        PadTarget.DpadUp => 0x0001,
        PadTarget.DpadDown => 0x0002,
        PadTarget.DpadLeft => 0x0004,
        PadTarget.DpadRight => 0x0008,
        PadTarget.Start => 0x0010,
        PadTarget.Back => 0x0020,
        PadTarget.LeftStickClick => 0x0040,
        PadTarget.RightStickClick => 0x0080,
        PadTarget.LeftBumper => 0x0100,
        PadTarget.RightBumper => 0x0200,
        PadTarget.Guide => 0x0400,
        PadTarget.ButtonA => 0x1000,
        PadTarget.ButtonB => 0x2000,
        PadTarget.ButtonX => 0x4000,
        PadTarget.ButtonY => 0x8000,
        _ => throw new ArgumentException($"{target} is not a button.", nameof(target)),
    };
}
