using ApexMapper.Core.Bindings;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;

namespace ApexMapper.Core.Engine;

/// <summary>One key as the engine sees it: slot, calibration if the sensor drives it, and its shaping.</summary>
public sealed record CompiledKey(
    ScanCode Key,
    int Slot,
    KeyCalibration? Calibration,
    Response.Response Response,
    float PressRampMs,
    float ReleaseRampMs)
{
    public bool AnalogDriven => Calibration is not null;
}

public sealed record CompiledKeyBinding(CompiledKey Key, PadTarget Target);

public sealed record CompiledAxisBinding(
    CompiledKey Negative,
    CompiledKey Positive,
    PadTarget Target,
    ConflictRule Conflict,
    AxisMode Mode,
    float RateMs,
    float ReturnMs);

/// <summary>
/// A profile resolved against a keyboard's calibration and sensor map, immutable for
/// the life of a session. Building it is the moment the "every analog key must be
/// calibrated" rule is enforced; a calibration that fails validation (a hand-edited
/// file) counts as missing.
/// </summary>
public sealed class CompiledProfile
{
    public IReadOnlyList<CompiledKeyBinding> Keys { get; }
    public IReadOnlyList<CompiledAxisBinding> Axes { get; }

    /// <summary>Analog-driven keys, for the store's flags and the poller's group list.</summary>
    public IReadOnlyList<CompiledKey> AnalogKeys { get; }

    /// <summary>Sensor groups the poller must read each cycle, ascending.</summary>
    public IReadOnlyList<int> NeededGroups { get; }

    private CompiledProfile(List<CompiledKeyBinding> keys, List<CompiledAxisBinding> axes)
    {
        Keys = keys;
        Axes = axes;
        AnalogKeys = keys.Select(k => k.Key).Concat(axes.SelectMany(a => new[] { a.Negative, a.Positive }))
            .Where(k => k.AnalogDriven).Distinct().ToList();
        NeededGroups = AnalogKeys.Select(k => SensorMap.GroupOf(k.Calibration!.SensorIndex)).Distinct().Order().ToList();
    }

    /// <summary>
    /// Compiles, or returns the analog keys that have no calibration. The caller shows
    /// them and refuses to start.
    /// </summary>
    public static CompiledProfile? TryCompile(
        Profile profile,
        SensorMap map,
        IReadOnlyDictionary<ScanCode, KeyCalibration> calibrations,
        out IReadOnlyList<ScanCode> uncalibrated)
    {
        if (profile.Validate() is { } error)
        {
            throw new ArgumentException(error, nameof(profile));
        }
        var analog = profile.AnalogKeys(map).ToHashSet();
        var missing = analog.Where(k => !calibrations.TryGetValue(k, out var cal) || !IsValid(cal)).ToList();
        uncalibrated = missing;
        if (missing.Count > 0)
        {
            return null;
        }

        CompiledKey Compile(ScanCode key, Response.Response response, float press, float release) =>
            new(key, key.Slot, analog.Contains(key) ? calibrations[key] : null, response, press, release);

        var keys = profile.Keys
            .Select(k => new CompiledKeyBinding(Compile(k.Key, k.Response, k.PressRampMs, k.ReleaseRampMs), k.Target))
            .ToList();
        var axes = profile.Axes
            .Select(a => new CompiledAxisBinding(
                Compile(a.NegativeKey, a.Response, a.PressRampMs, a.ReleaseRampMs),
                Compile(a.PositiveKey, a.Response, a.PressRampMs, a.ReleaseRampMs),
                a.Target, a.Conflict, a.Mode, a.RateMs, a.ReturnMs))
            .ToList();
        return new CompiledProfile(keys, axes);
    }

    private static bool IsValid(KeyCalibration cal) =>
        KeyCalibration.Validate(cal.Rest, cal.FullPress, cal.NoiseBand, cal.SensorIndex) is null;
}
