namespace ApexMapper.Core.Socd;

public struct SocdState
{
    public sbyte LastActivated;
    internal bool PrevNegActive;
    internal bool PrevPosActive;

    internal const sbyte None = 0;
    internal const sbyte Negative = -1;
    internal const sbyte Positive = 1;
}
