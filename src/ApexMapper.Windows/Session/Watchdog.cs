using System.Diagnostics;

namespace ApexMapper.Windows.Session;

public enum WatchdogVerdict
{
    None,

    /// <summary>The engine has not completed a tick for <see cref="Watchdog.StallMs"/>.</summary>
    EngineStalled,

    /// <summary>A game can no longer see the pad in its XInput slot.</summary>
    ControllerLost,

    /// <summary>Raw Input saw a key the hook never did: Windows removed the hook.</summary>
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
/// overruns its timeout. The hook and Raw Input see the same key events, each stamped
/// with the same OS event time (measured identical on this PC), and the hook sees each
/// one before Raw Input does. So Raw Input holding an event newer than the newest the
/// hook has seen, on <see cref="HookLossChecks"/> checks in a row, means the hook missed
/// it: one missed key-up is enough. Comparing event times rather than counts makes the
/// rule blind to how late either thread handles its events, and to events only one of
/// them sees. Raw Input's newest time when the watchdog is armed, when focus returns,
/// and at the first check after a reinstall is the baseline; nothing older counts.
/// Judged only while the game has focus: that is when a lost hook leaks keys into the
/// game, and the game is then known to be visible to both (an elevated window hides
/// input from the hook).
////// Each verdict is returned once; <see cref="HookReinstalled"/> re-enables hook loss.
/// </summary>
public sealed class Watchdog
{
    public const int StallMs = 200;
    public const int GapMs = 150;
    public const int HookLossChecks = 2;

    private readonly Func<long> _engineTick;
    private readonly Func<bool> _padPresent;
    private readonly Func<uint> _hookEventTime;
    private readonly Func<uint> _rawEventTime;
    private readonly Func<bool> _gameFocused;
    private readonly long _stallTicks;
    private readonly long _gapTicks;
    private int _armed;
    private long _lastCheck;
    private long _baseline;
    private bool _stallReported;
    private bool _controllerReported;
    private bool _hookReported;
    private uint _rawBaseline;
    private bool _rebasePending;
    private bool _focusedLastCheck;
    private int _missedChecks;

    /// <param name="engineTick">Timestamp of the engine's last completed tick.</param>
    /// <param name="padPresent">Whether the pad's XInput slot still had a controller at the engine's last look; never a driver call here.</param>
    /// <param name="hookEventTime">OS time of the newest event the hook has seen.</param>
    /// <param name="rawEventTime">OS time of the newest event Raw Input has seen.</param>
    /// <param name="gameFocused">The foreground flag the hook reads.</param>
    /// <param name="ticksPerMs">Clock ticks per millisecond; Stopwatch ticks by default.</param>
    public Watchdog(Func<long> engineTick, Func<bool> padPresent, Func<uint> hookEventTime, Func<uint> rawEventTime, Func<bool> gameFocused, double ticksPerMs = 0)
    {
        _engineTick = engineTick;
        _padPresent = padPresent;
        _hookEventTime = hookEventTime;
        _rawEventTime = rawEventTime;
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

    /// <summary>
    /// A new hook is going in. The baseline is taken at the next check, not now, so a key
    /// that lands between this call and the new hook's install is not held against it.
    /// </summary>
    public void HookReinstalled()
    {
        Volatile.Write(ref _rebasePending, true);
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
        if (!_focusedLastCheck || Volatile.Read(ref _rebasePending))
        {
            _focusedLastCheck = true;
            Rebase();
            return false;
        }
        var raw = _rawEventTime();
        if (!After(raw, _rawBaseline) || !After(raw, _hookEventTime()))
        {
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
        _rawBaseline = _rawEventTime();
        _missedChecks = 0;
        Volatile.Write(ref _rebasePending, false);
    }

    /// <summary>OS event times are milliseconds since boot and wrap every 49.7 days.</summary>
    private static bool After(uint later, uint earlier) => (int)(later - earlier) > 0;
}