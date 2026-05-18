using ApexMapper.Core.Pipeline;
using MessagePack;

namespace ApexMapper.Output.Ipc;

[MessagePackObject]
public sealed record ControlFrame : IFrame
{
    [Key(0)] public byte SchemaVersion { get; init; } = 1;
    [Key(1)] public long SequenceNumber { get; init; }
    [Key(2)] public long TimestampTicks { get; init; }
    [Key(3)] public PadStatePayload Payload { get; init; } = new();
}

[MessagePackObject]
public sealed record PadStatePayload
{
    [Key(0)]  public float LeftStickX { get; init; }
    [Key(1)]  public float LeftStickY { get; init; }
    [Key(2)]  public float RightStickX { get; init; }
    [Key(3)]  public float RightStickY { get; init; }
    [Key(4)]  public float LeftTrigger { get; init; }
    [Key(5)]  public float RightTrigger { get; init; }
    [Key(6)]  public bool ButtonA { get; init; }
    [Key(7)]  public bool ButtonB { get; init; }
    [Key(8)]  public bool ButtonX { get; init; }
    [Key(9)]  public bool ButtonY { get; init; }
    [Key(10)] public bool ButtonLB { get; init; }
    [Key(11)] public bool ButtonRB { get; init; }
    [Key(12)] public bool ButtonStart { get; init; }
    [Key(13)] public bool ButtonBack { get; init; }
    [Key(14)] public bool ButtonLS { get; init; }
    [Key(15)] public bool ButtonRS { get; init; }
    [Key(16)] public bool ButtonGuide { get; init; }
    [Key(17)] public bool DpadUp { get; init; }
    [Key(18)] public bool DpadDown { get; init; }
    [Key(19)] public bool DpadLeft { get; init; }
    [Key(20)] public bool DpadRight { get; init; }

    public static PadStatePayload From(in VirtualPadState state) => new()
    {
        LeftStickX   = state.LeftStickX,
        LeftStickY   = state.LeftStickY,
        RightStickX  = state.RightStickX,
        RightStickY  = state.RightStickY,
        LeftTrigger  = state.LeftTrigger,
        RightTrigger = state.RightTrigger,
        ButtonA      = state.ButtonA,
        ButtonB      = state.ButtonB,
        ButtonX      = state.ButtonX,
        ButtonY      = state.ButtonY,
        ButtonLB     = state.ButtonLB,
        ButtonRB     = state.ButtonRB,
        ButtonStart  = state.ButtonStart,
        ButtonBack   = state.ButtonBack,
        ButtonLS     = state.ButtonLS,
        ButtonRS     = state.ButtonRS,
        ButtonGuide  = state.ButtonGuide,
        DpadUp       = state.DpadUp,
        DpadDown     = state.DpadDown,
        DpadLeft     = state.DpadLeft,
        DpadRight    = state.DpadRight,
    };

    public VirtualPadState ToVirtualPadState() => new()
    {
        LeftStickX   = LeftStickX,
        LeftStickY   = LeftStickY,
        RightStickX  = RightStickX,
        RightStickY  = RightStickY,
        LeftTrigger  = LeftTrigger,
        RightTrigger = RightTrigger,
        ButtonA      = ButtonA,
        ButtonB      = ButtonB,
        ButtonX      = ButtonX,
        ButtonY      = ButtonY,
        ButtonLB     = ButtonLB,
        ButtonRB     = ButtonRB,
        ButtonStart  = ButtonStart,
        ButtonBack   = ButtonBack,
        ButtonLS     = ButtonLS,
        ButtonRS     = ButtonRS,
        ButtonGuide  = ButtonGuide,
        DpadUp       = DpadUp,
        DpadDown     = DpadDown,
        DpadLeft     = DpadLeft,
        DpadRight    = DpadRight,
    };
}
