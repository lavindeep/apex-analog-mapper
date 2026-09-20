namespace ApexMapper.Core.Sensors;

public enum LearnOutcome
{
    /// <summary>Exactly one sensor moved enough.</summary>
    Found,

    /// <summary>Nothing moved enough: the key may have no sensor, or was not pressed.</summary>
    NothingMoved,

    /// <summary>Two sensors moved comparably: more than one key was pressed, or the board does not match the assumptions.</summary>
    Ambiguous,
}

public sealed record LearnResult(LearnOutcome Outcome, int SensorIndex, int Delta, int SecondIndex, int SecondDelta);

/// <summary>
/// Finds which sensor a physical key is wired to: take a baseline at rest, watch every
/// sensor while the user holds the key, and pick the one that moved most. Used for
/// every key on unverified boards and to correct the default table on verified ones.
/// </summary>
public sealed class LearnStep
{
    /// <summary>A real press moves thousands of counts; this rejects noise and neighbours.</summary>
    public const int MinimumDelta = 300;

    /// <summary>The runner-up must move less than this fraction of the winner's delta.</summary>
    public const float AmbiguityRatio = 0.5f;

    private readonly ushort[] _baseline = new ushort[SensorProtocol.SensorCount];
    private readonly int[] _maxDelta = new int[SensorProtocol.SensorCount];

    public LearnStep(ReadOnlySpan<ushort> restBaseline)
    {
        if (restBaseline.Length != SensorProtocol.SensorCount)
        {
            throw new ArgumentException("Baseline must hold all 70 sensors.", nameof(restBaseline));
        }
        restBaseline.CopyTo(_baseline);
    }

    /// <summary>Feed one full snapshot of raw readings while the key is held.</summary>
    public void Observe(ReadOnlySpan<ushort> raw)
    {
        for (var i = 0; i < SensorProtocol.SensorCount; i++)
        {
            var delta = Math.Abs(raw[i] - _baseline[i]);
            if (delta > _maxDelta[i])
            {
                _maxDelta[i] = delta;
            }
        }
    }

    public LearnResult Result()
    {
        var best = -1;
        var second = -1;
        for (var i = 0; i < SensorProtocol.SensorCount; i++)
        {
            if (best < 0 || _maxDelta[i] > _maxDelta[best])
            {
                second = best;
                best = i;
            }
            else if (second < 0 || _maxDelta[i] > _maxDelta[second])
            {
                second = i;
            }
        }
        var bestDelta = _maxDelta[best];
        var secondDelta = second < 0 ? 0 : _maxDelta[second];
        if (bestDelta < MinimumDelta)
        {
            return new LearnResult(LearnOutcome.NothingMoved, best, bestDelta, second, secondDelta);
        }
        if (secondDelta >= bestDelta * AmbiguityRatio)
        {
            return new LearnResult(LearnOutcome.Ambiguous, best, bestDelta, second, secondDelta);
        }
        return new LearnResult(LearnOutcome.Found, best, bestDelta, second, secondDelta);
    }

    /// <summary>Try-it plausibility: did any sensor at all respond to a press?</summary>
    public bool AnythingMoved() => _maxDelta.Max() >= MinimumDelta;
}
