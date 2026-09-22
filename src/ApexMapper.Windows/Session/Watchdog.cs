using System.Diagnostics;

namespace ApexMapper.Windows.Session;

public enum WatchdogVerdict
{
    None,

    /// <summary>The engine has not completed a tick for <see cref="Watchdog.StallMs"/>.</summary>
    EngineStalled,

    /// <summary>A game can no longer see the pad in its XInput slot.</summary>
    ControllerLost,

    /// <summary>Raw Input counted a key the hook never saw: Windows removed the hook.</summary>
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
/// overruns its timeout. The hook and Raw Input see the same keys, so while the hook
/// lives, Raw Input's count minus the hook's stays where it was, apart from a moment of
/// skew when one counter has an event the other has not counted yet. A difference that
/// has grown on <see cref="HookLossChecks"/> consecutive checks is an event the hook
/// never saw: one missed key-up is enough. A difference that has shrunk on two checks
/// in a row (the hook saw something Raw Input never gets) becomes the new baseline.
/// Judged only while the game
/// has focus: that is when a lost hook leaks keys into the game, and the game is then
/// known to be visible to both (an elevated window hides input from the hook). The
/// baseline is taken afresh whenever focus returns, so what happened elsewhere does not
/// count.
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
    private long _baselineDifference;
    private long _previousDifference;
    private bool _focusedLastCheck;
    private int _missedChecks;

    /// <param name="engineTick">Timestamp of the engine's last completed tick.</param>
    /// <param name="padPresent">Whether the pad's XInput slot still had a controller at the engine's last look; never a driver call here.</param>
    /// <param name="hookEvents">The hook's callback count; <see cref="HookReinstalled"/> rebases it when a new hook starts from zero.</param>
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
        Rebase();
        Volatile.Write(ref _armed, 1);
    }

    public void Disarm() => Volatile.Write(ref _armed, 0);

    /// <summary>A new hook is in place: forget the silence that condemned the old one.</summary>
    public void HookReinstalled()
    {
        Rebase();
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
        if (!_gameFocused())
        {
            _focusedLastCheck = false;
            return false;
        }
        if (!_focusedLastCheck)
        {
            _focusedLastCheck = true;
            Rebase();
            return false;
        }
        var difference = _rawEvents() - _hookEvents();
        var previous = _previousDifference;
        _previousDifference = difference;
        if (difference <= _baselineDifference)
        {
            // Below the baseline on two checks in a row: the hook saw something Raw Input
            // never will. Below it on one only: the hook counted an event first, and Raw
            // Input catches up on the next check.
            if (difference < _baselineDifference && previous < _baselineDifference)
            {
                _baselineDifference = Math.Max(difference, previous);
            }
            _missedChecks = 0;
            return false;
        }
        _missedChecks++;
        if (_missedChecks < HookLossChecks || Volatile.Read(ref _hookReported))
        {
            return false;
        }
        Volatile.Write(ref _hookReported, true);
        return true;
    }

    private void Rebase()
    {
        _baselineDifference = _rawEvents() - _hookEvents();
        _previousDifference = _baselineDifference;
        _missedChecks = 0;
    }
}
