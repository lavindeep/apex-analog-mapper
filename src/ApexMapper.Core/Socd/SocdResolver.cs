namespace ApexMapper.Core.Socd;

public static class SocdResolver
{
    private const float ActiveThreshold = 1e-4f;

    // StrongerAnalogWins hysteresis band, expressed in normalized axis depth. Once a side wins
    // it keeps the axis until the opposing side leads by more than this margin, so sensor jitter
    // around equality can no longer flap the output between the two sides (or to neutral).
    private const float HysteresisBand = 0.02f;

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
            state.StrongerWinner = SocdState.None;
        }

        state.PrevNegActive = negActive;
        state.PrevPosActive = posActive;

        if (!negActive && !posActive) return 0f;
        // A single active side holds the axis, so it becomes the incumbent winner: when the
        // opposing side returns, the both-pressed hysteresis treats the held side as the one to
        // beat rather than letting a sub-band newcomer snap the axis to the wrong direction.
        if (!negActive)
        {
            state.StrongerWinner = SocdState.Positive;
            return positive;
        }
        if (!posActive)
        {
            state.StrongerWinner = SocdState.Negative;
            return -negative;
        }

        return mode switch
        {
            SocdMode.Neutral => 0f,
            SocdMode.StrongerAnalogWins => ResolveStrongerAnalog(negative, positive, ref state),
            SocdMode.LastInputWins => state.LastActivated switch
            {
                SocdState.Positive => positive,
                SocdState.Negative => -negative,
                _ => 0f,
            },
            _ => 0f,
        };
    }

    // Both sides are active. Keep the current winner until the other side leads by more than the
    // hysteresis band; with no established winner, require a clear lead before committing (an exact
    // or sub-band tie stays neutral).
    private static float ResolveStrongerAnalog(float negative, float positive, ref SocdState state)
    {
        var winner = state.StrongerWinner;
        if (winner == SocdState.Positive)
        {
            if (negative - positive > HysteresisBand) winner = SocdState.Negative;
        }
        else if (winner == SocdState.Negative)
        {
            if (positive - negative > HysteresisBand) winner = SocdState.Positive;
        }
        else
        {
            if (positive - negative > HysteresisBand) winner = SocdState.Positive;
            else if (negative - positive > HysteresisBand) winner = SocdState.Negative;
        }

        state.StrongerWinner = winner;
        return winner switch
        {
            SocdState.Positive => positive,
            SocdState.Negative => -negative,
            _ => 0f,
        };
    }
}
