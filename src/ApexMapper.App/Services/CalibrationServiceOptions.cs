namespace ApexMapper.App.Services;

/// <summary>Tunable parameters for <see cref="CalibrationService"/> capture windows.</summary>
public sealed record CalibrationServiceOptions(
    TimeSpan RestCaptureDuration,
    TimeSpan MaxCaptureDuration,
    TimeSpan NoiseCaptureDuration,
    int SamplesPerSecond)
{
    public static CalibrationServiceOptions Default { get; } = new(
        RestCaptureDuration: TimeSpan.FromSeconds(1),
        MaxCaptureDuration: TimeSpan.FromSeconds(1),
        NoiseCaptureDuration: TimeSpan.FromSeconds(1),
        SamplesPerSecond: 100);
}
