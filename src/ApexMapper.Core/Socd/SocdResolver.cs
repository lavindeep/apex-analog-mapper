namespace ApexMapper.Core.Socd;

public static class SocdResolver
{
    private const float ActiveThreshold = 1e-4f;

    public static float Resolve(SocdMode mode, float negative, float positive, ref SocdState state)
    {
        var negActive = negative > ActiveThreshold;
        var posActive = positive > ActiveThreshold;

        // Only update LastActivated when a side newly becomes active (transition from inactive).
        // If both sides were already tracked, don't re-tag either — the existing winner stands.
        var prevNeg = state.PrevNegActive;
        var prevPos = state.PrevPosActive;

        if (negActive && !prevNeg)
        {
            state.LastActivated = SocdState.Negative;
        }
        if (posActive && !prevPos)
        {
            state.LastActivated = SocdState.Positive;
        }
        if (!negActive && !posActive)
        {
            state.LastActivated = SocdState.None;
        }

        state.PrevNegActive = negActive;
        state.PrevPosActive = posActive;

        if (!negActive && !posActive) return 0f;
        if (!negActive) return positive;
        if (!posActive) return -negative;

        return mode switch
        {
            SocdMode.Neutral => 0f,
            SocdMode.StrongerAnalogWins => Math.Abs(positive - negative) < ActiveThreshold
                ? 0f
                : positive > negative ? positive : -negative,
            SocdMode.LastInputWins => state.LastActivated switch
            {
                SocdState.Positive => positive,
                SocdState.Negative => -negative,
                _ => 0f,
            },
            _ => 0f,
        };
    }
}
