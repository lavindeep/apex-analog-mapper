namespace ApexMapper.Core.Curves;

public sealed class DeadzoneCurve : ICurve
{
    private readonly ICurve _inner;
    private readonly float _innerDeadzone;
    private readonly float _outerDeadzone;
    private readonly float _range;

    public DeadzoneCurve(ICurve inner, float innerDeadzone, float outerDeadzone)
    {
        if (innerDeadzone < 0f || outerDeadzone > 1f || innerDeadzone >= outerDeadzone)
        {
            throw new ArgumentException(
                $"Invalid deadzones: inner={innerDeadzone}, outer={outerDeadzone}. " +
                "Required: 0 <= inner < outer <= 1.");
        }

        // Only the outer edge is checked for continuity, and deliberately so. The inner curve is
        // stretched across [inner, outer] and clamped to 1 at and beyond the outer edge; unless it
        // reaches 1 at its top the output cliffs at full deflection, which is never wanted, so it is
        // rejected. The inner edge is intentionally left free: an inner curve that starts above 0
        // is a deliberate anti-deadzone (an immediate minimum output past the deadzone), a common
        // gamepad shaping choice rather than a defect.
        if (MathF.Abs(inner.Map(1f) - 1f) > 1e-4f)
        {
            throw new ArgumentException(
                $"Inner curve must reach 1 at input 1 for boundary continuity; got {inner.Map(1f)}.",
                nameof(inner));
        }
        _inner = inner;
        _innerDeadzone = innerDeadzone;
        _outerDeadzone = outerDeadzone;
        _range = outerDeadzone - innerDeadzone;
    }

    public ICurve Inner => _inner;
    public float InnerDeadzone => _innerDeadzone;
    public float OuterDeadzone => _outerDeadzone;

    public float Map(float input)
    {
        if (input <= _innerDeadzone) return 0f;
        if (input >= _outerDeadzone) return 1f;
        return _inner.Map((input - _innerDeadzone) / _range);
    }
}
