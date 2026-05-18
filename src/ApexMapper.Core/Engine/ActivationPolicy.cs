namespace ApexMapper.Core.Engine;

public enum ActivationScope
{
    ForegroundOnly,
    Background,
}

public sealed record ActivationPolicy(
    ActivationScope Scope,
    int FocusLossDebounceMs,
    bool AutoEnable,
    bool RequiresOptInForProtectedGames)
{
    public static ActivationPolicy Default { get; } = new(
        Scope: ActivationScope.ForegroundOnly,
        FocusLossDebounceMs: 500,
        AutoEnable: false,
        RequiresOptInForProtectedGames: true);
}
