using System.Diagnostics;

namespace ApexMapper.Windows.Session;

public enum WatchdogVerdict
{
    None,

    /// <summary>The engine has not completed a tick for <see cref="Watchdog.StallMs"/>.</summary>
    EngineStalled,

    /// <summary>A game can no longer see the pad in its XInput slot.</summary>
    ControllerLost,

    /// <summary>Raw Input keeps seeing keys while the hook sees none: Windows removed the hook.</summary>
    HookLost,
}

/// <summary>
/// The safety checks the hook thread runs on its 50 ms timer, as a decision over
/// injected readings so each rule has a test with a fake clock. Armed when the session
/// is running (paused included, since the engine keeps ticking then), disarmed when it
/// starts stopping. <see cref="Arm"/> and <see cref="Disarm"/> run on the session
/// thread, <see cref="Check"/> on the hook thread.
///
/// Stall: the engine's last tick is older than <see cref="StallMs"/>. When the check
/// itself ran late by more than <see cref="GapMs"/>, the whole process was held up
/// (sleep, a debugger, a starved machine), not the engine alone, so the engine gets a
/// fresh <see cref="StallMs"/> from that moment instead of being declared stalled.
///
/// Hook loss: Windows removes a low-level hook without telling anyone when a callback
/// overruns its timeout. The hook and Raw Input see the same keys, so Raw Input
/// counting events over <see cref="HookLossChecks"/> consecutive checks while the
/// hook's count stands still means the hook is gone. Judged only while the game has
/// focus: that is when a lost hook leaks keys into the game, and the game is then known
/// to be visible to both (an elevated window hides input from the hook).
///
/// Each verdict is returned once; <see cref="HookReinstalled"/> re-enables hook loss.
/// </summary>
public sealed class Watchdog
{
    public const int StallMs = 200;
    public const int GapMs = 150;
    public const int HookLossChecks = 2;

    private readonly Func<long> _engineTick;
    private readonly Func<bool> _padPresent;
    private readonly Func<long> _hookEvents;
    private readonly Func<long> _rawEvents;
    private readonly Func<bool> _gameFocused;
    private readonly long _stallTicks;
    private readonly long _gapTicks;
    private int _armed;
    private long _lastCheck;
    private long _baseline;
    private bool _stallReported;
    private bool _controllerReported;
    private bool _hookReported;
    private long _hookSeen;
    private long _rawSeen;
    private int _silentChecks;

    /// <param name="engineTick">Timestamp of the engine's last completed tick.</param>
    /// <param name="padPresent">Whether the pad's XInput slot still has a controller.</param>
    /// <param name="hookEvents">The hook's callback count; any change counts as activity, so a reinstalled hook starting from zero is fine.</param>
    /// <param name="rawEvents">Raw Input's keyboard event count.</param>
    /// <param name="gameFocused">The foreground flag the hook reads.</param>
    /// <param name="ticksPerMs">Clock ticks per millisecond; Stopwatch ticks by default.</param>
    public Watchdog(Func<long> engineTick, Func<bool> padPresent, Func<long> hookEvents, Func<long> rawEvents, Func<bool> gameFocused, double ticksPerMs = 0)
    {
        _engineTick = engineTick;
        _padPresent = padPresent;
        _hookEvents = hookEvents;
        _rawEvents = rawEvents;
        _gameFocused = gameFocused;
        var perMs = ticksPerMs > 0 ? ticksPerMs : Stopwatch.Frequency / 1000d;
        _stallTicks = (long)(StallMs * perMs);
        _gapTicks = (long)(GapMs * perMs);
    }

    public bool IsArmed => Volatile.Read(ref _armed) != 0;

    public void Arm(long nowTicks)
    {
        _lastCheck = nowTicks;
        _baseline = nowTicks;
        _hookSeen = _hookEvents();
        _rawSeen = _rawEvents();
        _silentChecks = 0;
        Volatile.Write(ref _armed, 1);
    }

    public void Disarm() => Volatile.Write(ref _armed, 0);

    /// <summary>A new hook is in place: forget the silence that condemned the old one.</summary>
    public void HookReinstalled()
    {
        _silentChecks = 0;
        _hookSeen = _hookEvents();
        _rawSeen = _rawEvents();
        Volatile.Write(ref _hookReported, false);
    }

    public WatchdogVerdict Check(long nowTicks)
    {
        if (!IsArmed)
        {
            return WatchdogVerdict.None;
        }
        if (nowTicks - _lastCheck > _gapTicks)
        {
            _baseline = nowTicks;
        }
        _lastCheck = nowTicks;

        if (!_stallReported && nowTicks - Math.Max(_engineTick(), _baseline) > _stallTicks)
        {
            _stallReported = true;
            return WatchdogVerdict.EngineStalled;
        }
        if (!_controllerReported && !_padPresent())
        {
            _controllerReported = true;
            return WatchdogVerdict.ControllerLost;
        }
        return CheckHook() ? WatchdogVerdict.HookLost : WatchdogVerdict.None;
    }

    private bool CheckHook()
    {
        var hook = _hookEvents();
        var raw = _rawEvents();
        var hookMoved = hook != _hookSeen;
        var rawMoved = raw != _rawSeen;
        _hookSeen = hook;
        _rawSeen = raw;
        if (!_gameFocused() || hookMoved)
        {
            _silentChecks = 0;
            return false;
        }
        if (!rawMoved)
        {
            return false;
        }
        _silentChecks++;
        if (_silentChecks < HookLossChecks || Volatile.Read(ref _hookReported))
        {
            return false;
        }
        Volatile.Write(ref _hookReported, true);
        return true;
    }
}
