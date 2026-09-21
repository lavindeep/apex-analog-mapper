namespace ApexMapper.Core.Engine;

/// <summary>
/// A value that moves toward a target at a fixed rate in full-scale units per
/// millisecond. The duration is the time for a full 0 to 1 traverse, so a move from
/// 0.3 to 0 with a 100 ms duration takes 30 ms. A duration of zero snaps.
/// </summary>
public struct Ramp
{
    public float Value { get; private set; }

    /// <summary>Set the value directly. Analog drive does this every tick so a fallback starts from the last depth.</summary>
    public void Seed(float value) => Value = value;

    public void Reset() => Value = 0f;

    /// <summary>Moves toward the target. No elapsed time (zero, negative, or NaN dt) holds the value.</summary>
    public void Update(float target, float dtMs, float durationMs)
    {
        if (!float.IsFinite(dtMs) || dtMs <= 0f)
        {
            return;
        }
        if (durationMs <= 0f || !float.IsFinite(durationMs))
        {
            Value = target;
            return;
        }
        var step = dtMs / durationMs;
        var delta = target - Value;
        if (MathF.Abs(delta) <= step)
        {
            Value = target;
            return;
        }
        Value += MathF.Sign(delta) * step;
    }
}
