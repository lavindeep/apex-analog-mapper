using System.Diagnostics;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Timing;

namespace ApexMapper.Windows.Session;

/// <summary>
/// The engine thread: on every period of the 1 ms high-resolution timer (1.51 ms at
/// p50 as measured in stage 0) it runs the mapper over the latest sensor snapshot and
/// the hook's key state, offers the report to the pad, and publishes the tick's
/// timestamp for the watchdog.
///
/// Output is live while the game has focus and the session is not paused; otherwise
/// the engine keeps ticking (gates and fallback stay current) but offers neutral. When
/// output goes live again the mapper's ramp, handover, conflict and rate state is reset
/// first, so nothing from before the gap is released.
///
/// Every <see cref="PresenceCheckMs"/> the engine also reads the pad's XInput slot and
/// publishes <see cref="PadPresent"/> for the watchdog, which keeps that driver call off
/// the hook thread; a stalled engine stops the reads, and the stall rule covers it.
///
/// The thread ends when the pad is claimed (the watchdog or shutdown took it), when
/// <see cref="Stop"/> is called, or on a failure, which is kept in <see cref="Fault"/>
/// and raised through <see cref="Faulted"/>. The timer is created and disposed on this
/// thread, so a thread that never returns from a wedged call never has its handles
/// closed under it. A tick allocates nothing.
/// </summary>
public sealed class EngineLoop
{
    public const int PeriodMs = 1;
    public const int PresenceCheckMs = 50;

    /// <summary>How long <see cref="Stop"/> waits for the thread; the plan's shutdown bound for the engine.</summary>
    public const int JoinTimeoutMs = 50;

    private readonly Mapper _mapper;
    private readonly SensorSnapshot? _snapshot;
    private readonly VirtualPad _pad;
    private readonly ForegroundFlag _foreground;
    private readonly double _ticksPerMs = Stopwatch.Frequency / 1000d;
    private readonly long _presenceCheckTicks = PresenceCheckMs * Stopwatch.Frequency / 1000;
    private readonly Lock _timerLock = new();
    private HighResolutionTimer? _timer;
    private Thread? _thread;
    private PadReport _report;
    private long _previousTicks;
    private long _lastTickTicks;
    private long _nextPresenceCheck;
    private int _padPresent = 1;
    private bool _wasLive;
    private int _paused;
    private int _stopping;
    private string? _fault;

    /// <param name="snapshot">The sensor's shared snapshot, or null when the profile has no analog keys.</param>
    public EngineLoop(Mapper mapper, SensorSnapshot? snapshot, VirtualPad pad, ForegroundFlag foreground)
    {
        _mapper = mapper;
        _snapshot = snapshot;
        _pad = pad;
        _foreground = foreground;
    }

    /// <summary>Raised once on the engine thread with the reason when the engine stops on a failure. Must not wait for the engine.</summary>
    public event Action<string>? Faulted;

    /// <summary>Paused output is neutral; resuming resets the mapper's state like a return to the game.</summary>
    public bool Paused
    {
        get => Volatile.Read(ref _paused) != 0;
        set => Volatile.Write(ref _paused, value ? 1 : 0);
    }

    /// <summary>Stopwatch timestamp of the last completed tick, or zero before the first.</summary>
    public long LastTickTicks => Volatile.Read(ref _lastTickTicks);

    /// <summary>Whether a game could see the pad at the engine's last look, at most <see cref="PresenceCheckMs"/> ago.</summary>
    public bool PadPresent => Volatile.Read(ref _padPresent) != 0;

    /// <summary>Why the engine stopped on its own, if it did.</summary>
    public string? Fault => Volatile.Read(ref _fault);

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The engine was already started.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Releases the timer wait and joins, bounded by <see cref="JoinTimeoutMs"/>. Returns
    /// whether the thread has exited; false from the engine thread itself.
    /// </summary>
    public bool Stop()
    {
        Volatile.Write(ref _stopping, 1);
        lock (_timerLock)
        {
            _timer?.Stop();
        }
        var thread = _thread;
        if (thread is null)
        {
            return true;
        }
        if (thread.ManagedThreadId == Environment.CurrentManagedThreadId)
        {
            return false;
        }
        return thread.Join(JoinTimeoutMs);
    }

    /// <summary>One tick. False when the pad was claimed and the engine must stop. Internal so tests can drive it.</summary>
    internal bool Step(long nowTicks)
    {
        var dtMs = _previousTicks == 0 ? 0f : (float)((nowTicks - _previousTicks) / _ticksPerMs);
        _previousTicks = nowTicks;
        var live = _foreground.IsGameForeground && !Paused;
        if (live && !_wasLive)
        {
            _mapper.ResetState();
        }
        _wasLive = live;
        _mapper.Tick(_snapshot, nowTicks, dtMs, ref _report);
        if (!live)
        {
            _report = PadReport.Neutral;
        }
        if (!_pad.TrySubmit(_report, nowTicks))
        {
            return false;
        }
        if (nowTicks >= _nextPresenceCheck)
        {
            _nextPresenceCheck = nowTicks + _presenceCheckTicks;
            Volatile.Write(ref _padPresent, _pad.IsPresent() ? 1 : 0);
        }
        Volatile.Write(ref _lastTickTicks, nowTicks);
        return true;
    }

    private void Run()
    {
        HighResolutionTimer timer;
        try
        {
            timer = new HighResolutionTimer(PeriodMs);
        }
        catch (Exception e)
        {
            Fail("Stopped because Windows could not give the app a precise timer. " + e.Message);
            return;
        }
        lock (_timerLock)
        {
            _timer = timer;
        }
        try
        {
            while (Volatile.Read(ref _stopping) == 0 && timer.WaitNext())
            {
                if (!Step(Stopwatch.GetTimestamp()))
                {
                    break;
                }
            }
        }
        catch (PadException e)
        {
            Fail(e.Message);
        }
        catch (Exception e)
        {
            Fail(SessionEnd.For(EndReason.EngineFault).Message + " " + e.Message);
        }
        finally
        {
            lock (_timerLock)
            {
                _timer = null;
            }
            timer.Dispose();
        }
    }

    private void Fail(string reason)
    {
        Volatile.Write(ref _fault, reason);
        try
        {
            Faulted?.Invoke(reason);
        }
        catch (Exception)
        {
            // The session's handler only posts a stop; nothing here may take the thread down.
        }
    }
}
