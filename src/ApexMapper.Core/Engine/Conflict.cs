using ApexMapper.Core.Bindings;

namespace ApexMapper.Core.Engine;

/// <summary>Folds an axis pair's two shaped values into one signed value in -1..1.</summary>
public struct ConflictState
{
    private bool _previousNegative;
    private bool _previousPositive;
    private int _last;

    public void Reset()
    {
        _previousNegative = false;
        _previousPositive = false;
        _last = 0;
    }

    public float Resolve(ConflictRule rule, float negative, float positive)
    {
        var negativeActive = negative > 0f;
        var positiveActive = positive > 0f;
        if (negativeActive && !_previousNegative)
        {
            _last = -1;
        }
        if (positiveActive && !_previousPositive)
        {
            _last = 1;
        }
        _previousNegative = negativeActive;
        _previousPositive = positiveActive;

        if (!negativeActive && !positiveActive)
        {
            _last = 0;
            return 0f;
        }
        if (negativeActive != positiveActive)
        {
            return negativeActive ? -negative : positive;
        }
        return rule switch
        {
            ConflictRule.Neutral => 0f,
            _ => _last < 0 ? -negative : positive,
        };
    }
}
