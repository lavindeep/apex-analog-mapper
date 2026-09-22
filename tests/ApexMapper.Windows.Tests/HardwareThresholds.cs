using Xunit;

namespace ApexMapper.Windows.Tests;

/// <summary>
/// Limits for the hardware tests: measured on the maintainer's PC (Apex Pro TKL gen 1,
/// ViGEmBus 1.21) with the headroom stated per value.
/// </summary>
public static class HardwareThresholds
{
    /// <summary>1 ms waitable timer; measured p99 1.94 ms.</summary>
    public const double TimerPeriodP99Ms = 3;

    /// <summary>Two-group cycle measured p99 12.05 ms, plus a 6 ms canary in two percent of cycles.</summary>
    public static double SensorCycleP99Ms(int groups) => 6 * groups + 9;

    /// <summary>Submit to XInput readback, spin sampler; measured p99 0.032 ms.</summary>
    public const double ReadbackP99Ms = 1;

    /// <summary>Distinct XInput packets per second while changing every tick; measured 473 to 487.</summary>
    public const int XInputPacketsPerSecondMin = 400;

    /// <summary>Engine tick to readback; timer p99 plus readback.</summary>
    public const double LoopbackP99Ms = 4;

    /// <summary>Pad gone after TerminateProcess; measured at the first poll, 4 to 5 ms.</summary>
    public const int KillPadGoneMs = 500;

    /// <summary>Zero and unplug after the engine stalls: 200 ms staleness plus timer granularity plus disconnect.</summary>
    public const int WatchdogMs = 400;

    /// <summary>Hook callback p99.9; measured 0.32 ms pass-through.</summary>
    public const double HookCallbackP999Ms = 1;

    /// <summary>Callback events over 1 ms allowed per thousand.</summary>
    public const int HookExcursionsPerThousand = 1;
}
