namespace ApexMapper.Core.Curves;

public sealed class LinearCurve : ICurve
{
    public static LinearCurve Instance { get; } = new();

    public float Map(float input) => input < 0f ? 0f : input > 1f ? 1f : input;
}
