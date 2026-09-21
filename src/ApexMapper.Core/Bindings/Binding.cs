using ApexMapper.Core.Keys;
using ApexMapper.Core.Response;

namespace ApexMapper.Core.Bindings;

/// <summary>What an axis does when both of its keys are held.</summary>
public enum ConflictRule
{
    /// <summary>The key pressed most recently wins.</summary>
    LastInputWins,

    /// <summary>The axis centres.</summary>
    Neutral,
}

/// <summary>How key depth drives a stick axis.</summary>
public enum AxisMode
{
    /// <summary>Depth sets the stick position directly.</summary>
    Position,

    /// <summary>Depth sets how fast the stick moves toward full deflection; releasing returns it to centre.</summary>
    Rate,
}

/// <summary>
/// One key driving a button or a trigger. The response and ramps shape a trigger; a
/// button is digital and ignores both.
/// </summary>
public sealed record KeyBinding(
    ScanCode Key,
    PadTarget Target,
    Response.Response Response,
    float PressRampMs,
    float ReleaseRampMs)
{
    public static string? Validate(ScanCode key, PadTarget target, Response.Response? response, float pressRampMs, float releaseRampMs)
    {
        if (key.IsReserved)
        {
            return $"{key} is reserved (Ctrl, Alt, Windows, and F12 cannot be mapped).";
        }
        if (target.IsAxis())
        {
            return $"{target} needs two keys; use an axis binding.";
        }
        return ValidateResponse(response) ?? ValidateRamps(pressRampMs, releaseRampMs);
    }

    internal static string? ValidateResponse(Response.Response? response) =>
        response is null
            ? "A binding needs a response curve."
            : global::ApexMapper.Core.Response.Response.Validate(response.Exponent, response.Saturation, response.Deadzone);

    internal static string? ValidateRamps(float pressRampMs, float releaseRampMs) =>
        !float.IsFinite(pressRampMs) || pressRampMs < 0f || !float.IsFinite(releaseRampMs) || releaseRampMs < 0f
            ? "Ramps must be zero or a positive number of milliseconds."
            : null;

    public string? Validate() => Validate(Key, Target, Response, PressRampMs, ReleaseRampMs);
}

/// <summary>Two keys driving one stick axis: negative and positive directions.</summary>
public sealed record AxisBinding(
    ScanCode NegativeKey,
    ScanCode PositiveKey,
    PadTarget Target,
    Response.Response Response,
    float PressRampMs,
    float ReleaseRampMs,
    ConflictRule Conflict,
    AxisMode Mode,
    float RateMs,
    float ReturnMs)
{
    public const float DefaultRateMs = 150f;
    public const float DefaultReturnMs = 100f;

    public static string? Validate(ScanCode negativeKey, ScanCode positiveKey, PadTarget target, Response.Response? response, float pressRampMs, float releaseRampMs, float rateMs, float returnMs)
    {
        if (negativeKey.IsReserved || positiveKey.IsReserved)
        {
            return "Ctrl, Alt, Windows, and F12 cannot be mapped.";
        }
        if (negativeKey == positiveKey)
        {
            return "An axis needs two different keys.";
        }
        if (!target.IsAxis())
        {
            return $"{target} is not a stick axis.";
        }
        if (!float.IsFinite(rateMs) || rateMs <= 0f || !float.IsFinite(returnMs) || returnMs < 0f)
        {
            return "Rate must be positive and return must be zero or positive, in milliseconds.";
        }
        return KeyBinding.ValidateResponse(response) ?? KeyBinding.ValidateRamps(pressRampMs, releaseRampMs);
    }

    public string? Validate() => Validate(NegativeKey, PositiveKey, Target, Response, PressRampMs, ReleaseRampMs, RateMs, ReturnMs);
}
