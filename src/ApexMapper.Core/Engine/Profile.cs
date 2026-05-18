using ApexMapper.Core.Pipeline;

namespace ApexMapper.Core.Engine;

public sealed record Profile(
    string Id,
    string Name,
    DeviceMatcher Device,
    GameMatcher Game,
    ActivationPolicy Activation,
    IReadOnlyList<SingleKeyBinding> SingleBindings,
    IReadOnlyList<AxisPairBinding> AxisBindings,
    string? Notes);
