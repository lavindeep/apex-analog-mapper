namespace ApexMapper.Output.ViGEm;

/// <summary>
/// A virtual Xbox 360 pad frame in the units the ViGEm driver consumes: signed
/// 16-bit thumbstick axes, unsigned 8-bit triggers, and one bool per button.
/// This is a pure value with no driver dependency, so the whole translation from
/// <see cref="Core.Pipeline.VirtualPadState"/> is testable off Windows.
/// </summary>
public readonly record struct Xbox360Report
{
    public short LeftStickX { get; init; }
    public short LeftStickY { get; init; }
    public short RightStickX { get; init; }
    public short RightStickY { get; init; }
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }

    public bool A { get; init; }
    public bool B { get; init; }
    public bool X { get; init; }
    public bool Y { get; init; }
    public bool LeftShoulder { get; init; }
    public bool RightShoulder { get; init; }
    public bool Start { get; init; }
    public bool Back { get; init; }
    public bool LeftThumb { get; init; }
    public bool RightThumb { get; init; }
    public bool Guide { get; init; }
    public bool DpadUp { get; init; }
    public bool DpadDown { get; init; }
    public bool DpadLeft { get; init; }
    public bool DpadRight { get; init; }
}
