using System.Diagnostics;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Timing;

namespace ApexMapper.Windows.Session;

/// <summary>
/// The engine thread: every millisecond on the high-resolution timer it runs the
/// mapper over the latest sensor snapshot and the hook's key state, offers the report
/// to the pad, and publishes the tick's timestamp for the watchdog.
///
/// Output is live while the game has focus and the session is not paused; otherwise
/// the engine keeps ticking (gates and fallback stay current) but offers neutral. When
/// output goes live again the mapper's ramp, handover, conflict and rate state is reset
/// first, so nothing from before the gap is released.
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

    /// <summary>How long <see cref="Stop"/> waits for the thread; the plan's shutdown bound for the engine.</summary>
    public const int JoinTimeoutMs = 50;

    private readonly Mapper _mapper;
    private readonly SensorSnapshot? _snapshot;
    private readonly VirtualPad _pad;
    private readonly ForegroundFlag _foreground;
    private readonly double _ticksPerMs = Stopwatch.Frequency / 1000d;
    private readonly Lock _timerLock = new();
    private HighResolutionTimer? _timer;
    private Thread? _thread;
    private PadReport _report;
    private long _previousTicks;
    private long _lastTickTicks;
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
            Fail("The engine timer could not be created: " + e.Message);
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
            Fail("The engine stopped on an unexpected error: " + e.Message);
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
