using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Socd;

namespace ApexMapper.Core.Pipeline;

public sealed record AxisPairBinding(
    KeyId NegativeKey,
    KeyId PositiveKey,
    BindingTarget Target,
    ICurve Curve,
    float PressRampMs,
    float ReleaseRampMs,
    SocdMode Socd);
