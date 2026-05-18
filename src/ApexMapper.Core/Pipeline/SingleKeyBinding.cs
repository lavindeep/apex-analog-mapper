using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Pipeline;

public sealed record SingleKeyBinding(
    KeyId Source,
    BindingTarget Target,
    ICurve Curve,
    float PressRampMs,
    float ReleaseRampMs);
