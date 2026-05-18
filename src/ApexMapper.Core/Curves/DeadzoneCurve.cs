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
        _inner = inner;
        _innerDeadzone = innerDeadzone;
        _outerDeadzone = outerDeadzone;
        _range = outerDeadzone - innerDeadzone;
    }

    public float Map(float input)
    {
        if (input <= _innerDeadzone) return 0f;
        if (input >= _outerDeadzone) return 1f;
        return _inner.Map((input - _innerDeadzone) / _range);
    }
}
