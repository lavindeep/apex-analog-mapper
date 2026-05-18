using ApexMapper.Core.Keys;

namespace ApexMapper.Persistence.Devices;

public sealed record KeyCalibration(
    KeyId Key,
    float RestValue,
    float MaxPressValue,
    float NoiseBand);
