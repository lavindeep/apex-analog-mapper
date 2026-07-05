namespace ApexMapper.Core.Socd;

public struct SocdState
{
    public sbyte LastActivated;
    internal bool PrevNegActive;
    internal bool PrevPosActive;

    // Sticky winner for StrongerAnalogWins hysteresis: the side currently holding the axis,
    // kept until the other side overtakes it by more than the hysteresis band.
    internal sbyte StrongerWinner;

    internal const sbyte None = 0;
    internal const sbyte Negative = -1;
    internal const sbyte Positive = 1;
}
