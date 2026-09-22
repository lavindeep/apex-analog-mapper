using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;

namespace ApexMapper.Windows.Session;

/// <summary>
/// One mapping session at a time: start, run, pause, stop, following the design's
/// session table.
///
/// Every start, stop, pause, resume and hook reinstall runs on one session thread, in
/// the order it was asked for. Handlers on the hook, tracker, engine, pump, power and
/// thread-pool threads only post work to it, so none of them ever waits for its own
/// thread (a stop from a hook handler would otherwise join the hook thread from inside
/// it). A stop asked for while a start is still connecting the pad cancels the start
/// at its next step, and the start unwinds whatever it had started.
///
/// Two reactions cannot wait for the queue and run where the event lands. On the
/// tracker thread, gaining focus gates every key whose state is unknown before the
/// flag flips, and losing focus hands swallowed keys back to the desktop after it has
/// dropped. On the hook thread's 50 ms timer, the <see cref="Watchdog"/>: an engine
/// that stopped ticking gets its pad claimed at once, zeroed and unplugged on a
/// separate thread (so the hook thread never waits on the driver), and the hook
/// removed. A lost controller and a lost hook are posted.
///
/// Shutdown order, each step bounded by the part's own stop: disarm the watchdog; stop
/// the engine; zero and unplug the pad; remove the hook; stop the sensor; stop the
/// foreground tracker; restore the GC latency mode.
/// </summary>
public sealed class MappingSession : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for the session thread to finish the last stop.</summary>
    public const int DisposeTimeoutMs = 10_000;

    private readonly SessionServices _services;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private Parts? _parts;
    private CancellationTokenSource? _starting;
    private SessionEnd? _pendingStop;
    private SessionEnd? _lastEnd;
    private int _state;
    private int _handlerFaults;
    private int _disposed;

    public MappingSession(SessionServices services)
    {
        _services = services;
        _thread = new Thread(Pump) { IsBackground = true, Name = "apex-session" };
        _thread.Start();
    }

    /// <summary>Raised on the session thread after every state change. Must not wait for the session.</summary>
    public event Action<SessionState>? StateChanged;

    public SessionState State => (SessionState)Volatile.Read(ref _state);

    /// <summary>Why the last session stopped, or why the last start was refused or failed.</summary>
    public SessionEnd? LastEnd => Volatile.Read(ref _lastEnd);

    /// <summary>Exceptions caught on the session thread: state handlers and unexpected failures.</summary>
    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    /// <summary>For tests: the hook of the running session.</summary>
    internal KeyboardHook? Hook => _parts is { } parts ? Volatile.Read(ref parts.Hook) : null;

    /// <summary>For tests: the key state of the running session.</summary>
    internal KeyStateStore? Store => _parts?.Store;

    /// <summary>
    /// Why a session could not start right now, or null when it can: the keyboard is
    /// connected, the driver is running, and the game is running. Calibration is proven
    /// by the <see cref="CompiledProfile"/> a request carries.
    /// </summary>
    public SessionEnd? WhyNotStartable(Guid keyboard, string gamePath)
    {
        var refusal = CheckPreconditions(keyboard, gamePath, out var game);
        game?.Dispose();
        return refusal;
    }

    /// <summary>Completes with null once the session is running (or paused), or with why it did not start.</summary>
    public Task<SessionEnd?> StartAsync(SessionRequest request)
    {
        var done = new TaskCompletionSource<SessionEnd?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new CancellationTokenSource();
        Volatile.Write(ref _pendingStop, null);
        Volatile.Write(ref _starting, cancel);
        var posted = Post(() =>
        {
            try
            {
                done.TrySetResult(DoStart(request, cancel.Token));
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });
        if (!posted)
        {
            done.TrySetResult(new SessionEnd(EndReason.StartFailed, "The app is closing."));
        }
        return done.Task;
    }

    /// <summary>Asks for a stop from any thread, handlers included, and returns at once.</summary>
    public void RequestStop(EndReason reason) => RequestStop(null, SessionEnd.For(reason));

    /// <summary>Completes when the session is idle.</summary>
    public Task StopAsync(EndReason reason)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var end = SessionEnd.For(reason);
        CancelStart(end);
        var posted = Post(() =>
        {
            try
            {
                DoStop(null, end);
            }
            finally
            {
                done.TrySetResult();
            }
        });
        if (!posted)
        {
            done.TrySetResult();
        }
        return done.Task;
    }

    public SessionStatus Status()
    {
        var parts = Volatile.Read(ref _parts);
        if (parts is null)
        {
            return new SessionStatus(State, LastEnd, false, false, 0, null, false, 0, 0);
        }
        var focus = parts.Flag.IsGameForeground;
        var foreground = Volatile.Read(ref parts.Foreground)?.Current;
        var fallback = Volatile.Read(ref parts.Engine) is null ? 0 : parts.Mapper.FallbackCount;
        string? problem = null;
        if (fallback > 0)
        {
            problem = parts.Poller is { State: not PollerState.Running, FaultReason: { } reason }
                ? reason
                : "The keyboard's analog readings are arriving late.";
        }
        var awaitingRelease = false;
        foreach (var key in parts.MappedKeys)
        {
            awaitingRelease |= focus && parts.Store.IsGated(key.Slot);
        }
        return new SessionStatus(
            State,
            LastEnd,
            focus,
            foreground is { IsGame: true, Elevation: not Elevation.Visible },
            fallback,
            problem,
            awaitingRelease,
            Volatile.Read(ref parts.Pad)?.SubmitCount ?? 0,
            Volatile.Read(ref parts.HookReinstalls));
    }

    /// <summary>Stops a running session (as the app closing) and ends the session thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        RequestStop(null, SessionEnd.For(EndReason.AppClosing));
        _work.CompleteAdding();
        _thread.Join(DisposeTimeoutMs);
    }

    private void Pump()
    {
        foreach (var work in _work.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _handlerFaults);
            }
        }
    }

    private bool Post(Action work)
    {
        try
        {
            _work.Add(work);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <param name="expected">The session the request belongs to; null for whichever is running. A stale request from an earlier session is ignored.</param>
    private void RequestStop(Parts? expected, SessionEnd end)
    {
        if (expected is null)
        {
            CancelStart(end);
        }
        Post(() => DoStop(expected, end));
    }

    private void CancelStart(SessionEnd end)
    {
        Interlocked.CompareExchange(ref _pendingStop, end, null);
        Volatile.Read(ref _starting)?.Cancel();
    }

    private SessionEnd? CheckPreconditions(Guid keyboard, string gamePath, out IGameProcess? game)
    {
        game = null;
        if (!KeyboardPresent(keyboard))
        {
            return SessionEnd.For(EndReason.KeyboardMissing);
        }
        switch (_services.DriverState())
        {
            case DriverState.Missing:
                return SessionEnd.For(EndReason.DriverMissing);
            case DriverState.NotStarted:
                return SessionEnd.For(EndReason.DriverNotStarted);
        }
        game = _services.FindGame(gamePath);
        return game is null ? SessionEnd.For(EndReason.GameNotRunning) : null;
    }

    private bool KeyboardPresent(Guid keyboard) => _services.Keyboards.Current.Any(k => k.ContainerId == keyboard);

    private SessionEnd? DoStart(SessionRequest request, CancellationToken cancel)
    {
        if (State != SessionState.Idle)
        {
            return new SessionEnd(EndReason.StartFailed, "A session is already running.");
        }
        SetState(SessionState.Starting);
        var refusal = CheckPreconditions(request.Keyboard, request.GamePath, out var game);
        if (refusal is null && cancel.IsCancellationRequested)
        {
            refusal = Volatile.Read(ref _pendingStop) ?? SessionEnd.For(EndReason.UserStop);
        }
        if (refusal is not null)
        {
            game?.Dispose();
            return End(refusal);
        }
        var parts = new Parts(request, game!, _services.SwallowInjected);
        Volatile.Write(ref _parts, parts);
        try
        {
            StartParts(parts, cancel);
            return null;
        }
        catch (Exception e)
        {
            var end = e switch
            {
                OperationCanceledException => Volatile.Read(ref _pendingStop) ?? SessionEnd.For(EndReason.UserStop),
                PadException pad => new SessionEnd(EndReason.PadFailed, pad.Message),
                _ => new SessionEnd(EndReason.StartFailed, "The session could not start: " + e.Message),
            };
            SetState(SessionState.Stopping);
            var problem = Teardown(parts);
            Volatile.Write(ref _parts, null);
            return End(WithProblem(end, problem));
        }
    }

    /// <summary>Session thread. Throws on the first part that fails; the caller tears down what was started.</summary>
    private void StartParts(Parts parts, CancellationToken cancel)
    {
        var request = parts.Request;
        parts.PreviousLatency = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        parts.LatencySet = true;

        if (request.Profile.NeededGroups.Count > 0)
        {
            parts.Snapshot = new SensorSnapshot();
            parts.Poller = _services.CreatePoller(request.Keyboard, parts.Snapshot, PollerConfig.For(request.Profile.NeededGroups, request.Signatures));
            parts.Poller.Start();
        }

        Volatile.Write(ref parts.Pad, _services.ConnectPad(cancel));
        cancel.ThrowIfCancellationRequested();

        var foreground = _services.CreateForeground(parts.Flag);
        Volatile.Write(ref parts.Foreground, foreground);
        foreground.GamePath = request.GamePath;
        foreground.Changed += info => OnFocusChanged(parts, info);
        foreground.Start();

        InstallHook(parts);
        parts.Store.GateUnknown();

        var engine = new EngineLoop(parts.Mapper, parts.Snapshot, parts.Pad!, parts.Flag);
        engine.Faulted += reason => RequestStop(parts, new SessionEnd(EndReason.EngineFault, reason));
        Volatile.Write(ref parts.Engine, engine);
        engine.Start();

        parts.CrashGuard = new CrashGuard(() => Emergency(parts));
        parts.Game.OnExit(() => RequestStop(parts, SessionEnd.For(EndReason.GameExited)));
        parts.OnPower = () => RequestStop(parts, SessionEnd.For(EndReason.SleepOrWake));
        _services.Power.SleepOrWake += parts.OnPower;
        parts.OnKeyboards = _ => Post(() => OnKeyboards(parts));
        _services.Keyboards.Changed += parts.OnKeyboards;
        cancel.ThrowIfCancellationRequested();

        var pad = parts.Pad!;
        var watchdog = new Watchdog(
            () => engine.LastTickTicks,
            pad.IsPresent,
            () => Volatile.Read(ref parts.Hook)?.EventCount ?? 0,
            _services.RawInputEvents,
            () => parts.Flag.IsGameForeground);
        watchdog.Arm(Stopwatch.GetTimestamp());
        Volatile.Write(ref parts.Watchdog, watchdog);

        var present = KeyboardPresent(request.Keyboard);
        engine.Paused = !present;
        SetState(present ? SessionState.Running : SessionState.Paused);
    }

    private void InstallHook(Parts parts)
    {
        var hook = new KeyboardHook(parts.Store, parts.Policy, parts.Flag, parts.MappedKeys)
        {
            StopRequested = () => RequestStop(parts, SessionEnd.For(EndReason.Hotkey)),
            Timer = () => OnHookTimer(parts),
        };
        Volatile.Write(ref parts.Hook, hook);
        hook.Start();
    }

    /// <summary>Tracker thread. On gain this runs before the flag flips; on loss, after it dropped.</summary>
    private static void OnFocusChanged(Parts parts, ForegroundInfo info)
    {
        if (info.GameHasFocus)
        {
            parts.Store.GateUnknown();
        }
        else
        {
            parts.Policy.ForegroundLost();
        }
    }

    /// <summary>Hook thread, every 50 ms. Allocates only when a check fires.</summary>
    private void OnHookTimer(Parts parts)
    {
        var watchdog = Volatile.Read(ref parts.Watchdog);
        if (watchdog is null)
        {
            return;
        }
        switch (watchdog.Check(Stopwatch.GetTimestamp()))
        {
            case WatchdogVerdict.EngineStalled:
                var pad = parts.Pad!;
                pad.Claim();
                new Thread(() => pad.Unplug()) { IsBackground = true, Name = "apex-unplug" }.Start();
                // From the hook thread this returns at once; the hook goes when this timer message returns.
                Volatile.Read(ref parts.Hook)?.Stop();
                RequestStop(parts, SessionEnd.For(EndReason.EngineStalled));
                break;
            case WatchdogVerdict.ControllerLost:
                RequestStop(parts, SessionEnd.For(EndReason.ControllerDisconnected));
                break;
            case WatchdogVerdict.HookLost:
                Post(() => ReinstallHook(parts));
                break;
        }
    }

    /// <summary>Session thread. Pause when the selected keyboard disappears; on its return, gate what is unknown, then resume.</summary>
    private void OnKeyboards(Parts parts)
    {
        if (!ReferenceEquals(Volatile.Read(ref _parts), parts) || State is not (SessionState.Running or SessionState.Paused))
        {
            return;
        }
        var present = KeyboardPresent(parts.Request.Keyboard);
        if (!present && State == SessionState.Running)
        {
            parts.Engine!.Paused = true;
            SetState(SessionState.Paused);
        }
        else if (present && State == SessionState.Paused)
        {
            parts.Store.GateUnknown();
            parts.Engine!.Paused = false;
            SetState(SessionState.Running);
        }
    }

    /// <summary>Session thread. Windows removed the hook: gate what is unknown, put a new hook in, and count it.</summary>
    private void ReinstallHook(Parts parts)
    {
        if (!ReferenceEquals(Volatile.Read(ref _parts), parts) || State is not (SessionState.Running or SessionState.Paused))
        {
            return;
        }
        parts.Store.GateUnknown();
        var old = Volatile.Read(ref parts.Hook);
        if (old is not null && !old.Stop())
        {
            DoStop(parts, SessionEnd.For(EndReason.HookFailed));
            return;
        }
        old?.Dispose();
        try
        {
            var watchdog = parts.Watchdog!;
            var hook = new KeyboardHook(parts.Store, parts.Policy, parts.Flag, parts.MappedKeys)
            {
                StopRequested = old?.StopRequested,
                Timer = old?.Timer,
            };
            Volatile.Write(ref parts.Hook, hook);
            watchdog.HookReinstalled();
            hook.Start();
            Interlocked.Increment(ref parts.HookReinstalls);
        }
        catch (Exception e)
        {
            DoStop(parts, new SessionEnd(EndReason.HookFailed, SessionEnd.For(EndReason.HookFailed).Message + " " + e.Message));
        }
    }

    /// <summary>Session thread. The first stop for a session wins; later ones, and stale ones from an earlier session, do nothing.</summary>
    private void DoStop(Parts? expected, SessionEnd end)
    {
        var parts = Volatile.Read(ref _parts);
        if (parts is null || (expected is not null && !ReferenceEquals(parts, expected)))
        {
            return;
        }
        SetState(SessionState.Stopping);
        var problem = Teardown(parts);
        Volatile.Write(ref _parts, null);
        End(WithProblem(end, problem));
    }

    private SessionEnd End(SessionEnd end)
    {
        Volatile.Write(ref _lastEnd, end);
        SetState(SessionState.Idle);
        return end;
    }

    private static SessionEnd WithProblem(SessionEnd end, string? problem) =>
        problem is null ? end : end with { Message = end.Message + " " + problem };

    /// <summary>
    /// The shutdown order. Every step runs even when an earlier one fails; what went
    /// wrong comes back as one sentence for the status card, or null.
    /// </summary>
    private string? Teardown(Parts parts)
    {
        var problems = new List<string>();
        void Step(Func<string?> step)
        {
            try
            {
                if (step() is { } problem)
                {
                    problems.Add(problem);
                }
            }
            catch (Exception e)
            {
                problems.Add(e.Message);
            }
        }

        Step(() =>
        {
            Volatile.Read(ref parts.Watchdog)?.Disarm();
            parts.CrashGuard?.Dispose();
            if (parts.OnPower is not null)
            {
                _services.Power.SleepOrWake -= parts.OnPower;
            }
            if (parts.OnKeyboards is not null)
            {
                _services.Keyboards.Changed -= parts.OnKeyboards;
            }
            parts.Game.Dispose();
            return null;
        });
        var engineStopped = true;
        Step(() =>
        {
            engineStopped = Volatile.Read(ref parts.Engine)?.Stop() ?? true;
            return engineStopped ? null : "The engine thread did not stop.";
        });
        Step(() =>
        {
            if (Volatile.Read(ref parts.Pad) is not { } pad)
            {
                return null;
            }
            var unplugged = pad.Unplug();
            // A wedged engine may still return into the driver: its handle stays open until the process ends.
            if (unplugged && engineStopped)
            {
                pad.Dispose();
            }
            return unplugged ? pad.UnplugError : "The virtual controller could not be removed in time.";
        });
        Step(() =>
        {
            if (Volatile.Read(ref parts.Hook) is not { } hook)
            {
                return null;
            }
            var stopped = hook.Stop();
            hook.Dispose();
            return stopped ? null : "The keyboard hook thread did not stop.";
        });
        Step(() =>
        {
            if (parts.Poller is not { } poller)
            {
                return null;
            }
            var stopped = poller.Stop();
            poller.Dispose();
            return stopped ? null : "The sensor thread did not stop.";
        });
        Step(() =>
        {
            if (Volatile.Read(ref parts.Foreground) is not { } foreground)
            {
                return null;
            }
            var stopped = foreground.Stop();
            foreground.Dispose();
            return stopped ? null : "The foreground tracker thread did not stop.";
        });
        Step(() =>
        {
            RestoreLatency(parts);
            return null;
        });
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>The crash guard's path, on whatever thread is failing: pad, hook, GC mode, nothing else.</summary>
    private static void Emergency(Parts parts)
    {
        Volatile.Read(ref parts.Pad)?.Unplug();
        Volatile.Read(ref parts.Hook)?.Stop();
        RestoreLatency(parts);
    }

    private static void RestoreLatency(Parts parts)
    {
        if (parts.LatencySet)
        {
            parts.LatencySet = false;
            GCSettings.LatencyMode = parts.PreviousLatency;
        }
    }

    private void SetState(SessionState state)
    {
        Volatile.Write(ref _state, (int)state);
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    /// <summary>Everything one session owns. A part left null was never started.</summary>
    private sealed class Parts
    {
        public readonly SessionRequest Request;
        public readonly IGameProcess Game;
        public readonly KeyStateStore Store = new();
        public readonly ForegroundFlag Flag = new();
        public readonly HookPolicy Policy;
        public readonly Mapper Mapper;
        public readonly ScanCode[] MappedKeys;
        public GCLatencyMode PreviousLatency;
        public bool LatencySet;
        public SensorSnapshot? Snapshot;
        public SensorPoller? Poller;
        public VirtualPad? Pad;
        public IForegroundSource? Foreground;
        public KeyboardHook? Hook;
        public EngineLoop? Engine;
        public Watchdog? Watchdog;
        public CrashGuard? CrashGuard;
        public Action? OnPower;
        public Action<IReadOnlyList<KeyboardInfo>>? OnKeyboards;
        public int HookReinstalls;

        public Parts(SessionRequest request, IGameProcess game, bool swallowInjected)
        {
            Request = request;
            Game = game;
            Policy = new HookPolicy { SwallowInjected = swallowInjected };
            Mapper = new Mapper(request.Profile, Store);
            MappedKeys = request.Profile.Keys.Select(k => k.Key.Key)
                .Concat(request.Profile.Axes.SelectMany(a => new[] { a.Negative.Key, a.Positive.Key }))
                .Distinct()
                .ToArray();
        }
    }
}
