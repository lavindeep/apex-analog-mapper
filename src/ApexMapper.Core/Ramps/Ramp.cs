namespace ApexMapper.Core.Ramps;

public sealed class Ramp
{
    private readonly float _pressMs;
    private readonly float _releaseMs;
    private float _value;

    public Ramp(float pressMs, float releaseMs)
    {
        if (pressMs < 0f) throw new ArgumentException("Press duration cannot be negative.", nameof(pressMs));
        if (releaseMs < 0f) throw new ArgumentException("Release duration cannot be negative.", nameof(releaseMs));
        _pressMs = pressMs;
        _releaseMs = releaseMs;
    }

    public float Value => _value;

    public void Update(bool pressed, float dtMs)
    {
        var target = pressed ? 1f : 0f;
        var duration = pressed ? _pressMs : _releaseMs;
        if (duration <= 0f)
        {
            _value = target;
            return;
        }
        var step = dtMs / duration;
        var delta = target - _value;
        if (Math.Abs(delta) <= step)
        {
            _value = target;
            return;
        }
        _value += Math.Sign(delta) * step;
    }

    public void Reset() => _value = 0f;
}
