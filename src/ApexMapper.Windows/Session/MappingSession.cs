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
/// Three reactions cannot wait for the queue and run where the event lands. On the
/// tracker thread, gaining focus gates every key whose state is unknown before the
/// flag flips, and losing focus hands swallowed keys back to the desktop after it has
/// dropped. On the pump thread, a keyboard's removal pauses the engine's output at once;
/// the debounced keyboard list decides, on the session thread, whether to resume. On
/// the hook thread's 50 ms timer, the <see cref="Watchdog"/>: an engine that stopped
/// ticking gets its pad unplugged on a worker (the hook thread never waits on the
/// driver) and the hook removed. A lost controller and a lost hook are posted. A 250 ms
/// health check on the session thread notices a hook or a tracker whose thread has
/// ended, since that takes the watchdog with it.
///
/// Shutdown order: disarm the watchdog; stop the engine; zero and unplug the pad;
/// remove the hook; stop the sensor; stop the foreground tracker; restore the GC latency
/// mode. Each step is bounded by the part's own stop. The keyboard is back to normal the
/// moment the hook is gone: a key still held then reaches the game as a keyboard key,
/// which the maintainer chose over any delay (stage 3 ledger, Q1).
/// </summary>
public sealed class MappingSession : IMappingSession, IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for the session thread to finish the last stop.</summary>
    public const int DisposeTimeoutMs = 15_000;

    public const int HealthCheckMs = 250;

    /// <summary>How often a session started before its game looks for the game's process.</summary>
    public const int GameSearchMs = 1000;

    /// <summary>The default for <see cref="SessionServices.GameRelaunchGrace"/>.</summary>
    public const int GameRelaunchGraceMs = 5000;

    /// <summary>Handler faults on the hook thread within one health check that mean its safety checks are broken.</summary>
    public const int HealthFaultLimit = 3;

    private readonly SessionServices _services;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private Parts? _parts;
    private CancellationTokenSource? _starting;
    private SessionEnd? _pendingStop;
    private SessionEnd? _lastEnd;
    private int _state;
    private int _startPending;
    private int _restartRequired;
    private int _handlerFaults;
    private int _disposed;

    public MappingSession(SessionServices services)
    {
        _services = services;
        _thread = new Thread(Pump) { IsBackground = true, Name = "apex-session" };
        _thread.Start();
    }

    /// <summary>
    /// Raised on the session thread after every state change. Must not wait for the
    /// session: a UI marshals it with a non-blocking post, never a blocking invoke, or a
    /// UI thread waiting in <see cref="Dispose"/> would hold up the stop.
    /// </summary>
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

    /// <summary>For tests: the crash guard of the running session.</summary>
    internal CrashGuard? CrashGuard => _parts?.CrashGuard;

    /// <summary>
    /// A controller from an earlier session could not be removed (a hung driver). Start
    /// is refused until the app restarts, which is the only thing that clears it; the
    /// window offers the restart.
    /// </summary>
    public bool RestartRequired => Volatile.Read(ref _restartRequired) != 0;

    /// <summary>
    /// Why a session could not start right now, or null when it can: the keyboard is
    /// connected, the driver is running, and no stuck controller needs a restart. The game
    /// need not be running yet; calibration is proven by the <see cref="CompiledProfile"/>
    /// a request carries.
    /// </summary>
    public SessionEnd? WhyNotStartable(Guid keyboard) => CheckPreconditions(keyboard);

    /// <summary>
    /// Completes with null once the session is running (or paused), or with why it did
    /// not start. Refused at once while another start is in flight or after
    /// <see cref="Dispose"/>.
    /// </summary>
    public Task<SessionEnd?> StartAsync(SessionRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromResult<SessionEnd?>(new SessionEnd(EndReason.StartFailed, "The app is closing."));
        }
        if (Interlocked.CompareExchange(ref _startPending, 1, 0) != 0)
        {
            return Task.FromResult<SessionEnd?>(new SessionEnd(EndReason.StartFailed, "A start is already in progress."));
        }
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
            finally
            {
                Volatile.Write(ref _startPending, 0);
            }
        });
        if (!posted)
        {
            Volatile.Write(ref _startPending, 0);
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
        if (Volatile.Read(ref _parts) is { } parts)
        {
            Volatile.Write(ref parts.StopRequested, true);
        }
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

    /// <summary>A reading for the status card. Call it from one thread at a time: the cycle percentiles share a scratch buffer.</summary>
    public SessionStatus Status()
    {
        var parts = Volatile.Read(ref _parts);
        if (parts is null)
        {
            return new SessionStatus(State, LastEnd, false, false, false, 0, null, false, 0, 0, RestartRequired);
        }
        var focus = parts.Flag.IsGameForeground;
        var foreground = Volatile.Read(ref parts.Foreground)?.Current;
        var poller = Volatile.Read(ref parts.Poller);
        var fallback = Volatile.Read(ref parts.Engine) is null ? 0 : parts.Mapper.FallbackCount;
        string? problem = null;
        if (fallback > 0)
        {
            problem = poller is { State: not PollerState.Running, FaultReason: { } reason }
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
            Volatile.Read(ref parts.Game) is not null,
            focus,
            foreground is { IsGame: true, Elevation: not Elevation.Visible },
            fallback,
            problem,
            awaitingRelease,
            Volatile.Read(ref parts.Pad)?.SubmitCount ?? 0,
            Volatile.Read(ref parts.HookReinstalls),
            RestartRequired,
            poller?.Stats.P50 ?? float.NaN,
            poller?.Stats.P99 ?? float.NaN,
            KeysAtLimit(parts));
    }

    /// <summary>Analog keys whose calibrated full press is short of the sensor's limit, reading at it in a fresh reading.</summary>
    private static List<ScanCode>? KeysAtLimit(Parts parts)
    {
        if (Volatile.Read(ref parts.Snapshot) is not { } snapshot)
        {
            return null;
        }
        var keys = new List<ScanCode>();
        for (var attempt = 0; attempt < SensorSnapshot.MaxReadAttempts; attempt++)
        {
            if (!snapshot.TryBeginRead(out var generation))
            {
                continue;
            }
            keys.Clear();
            var fresh = snapshot.IsFresh(Stopwatch.GetTimestamp());
            foreach (var key in parts.Request.Profile.AnalogKeys)
            {
                if (fresh && key.Calibration is { IsClipping: false } calibration && snapshot.WasRead(calibration.SensorIndex)
                    && calibration.IsAtLimit(snapshot.Raw[calibration.SensorIndex]))
                {
                    keys.Add(key.Key);
                }
            }
            if (snapshot.EndRead(generation))
            {
                return keys;
            }
        }
        return null;
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
        // From here on nothing queued ahead of the stop may reinstall a hook or resume.
        var target = expected ?? Volatile.Read(ref _parts);
        if (target is not null)
        {
            Volatile.Write(ref target.StopRequested, true);
        }
        Post(() => DoStop(expected, end));
    }

    private void CancelStart(SessionEnd end)
    {
        Interlocked.CompareExchange(ref _pendingStop, end, null);
        Volatile.Read(ref _starting)?.Cancel();
    }

    private SessionEnd? CheckPreconditions(Guid keyboard)
    {
        if (RestartRequired)
        {
            return SessionEnd.For(EndReason.RestartRequired);
        }
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
        return null;
    }

    private bool KeyboardPresent(Guid keyboard) => _services.Keyboards.Current.Any(k => k.ContainerId == keyboard);

    private SessionEnd PendingStop() => Volatile.Read(ref _pendingStop) ?? SessionEnd.For(EndReason.UserStop);

    private SessionEnd? DoStart(SessionRequest request, CancellationToken cancel)
    {
        if (State != SessionState.Idle)
        {
            return new SessionEnd(EndReason.StartFailed, "A session is already running.");
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new SessionEnd(EndReason.StartFailed, "The app is closing.");
        }
        SetState(SessionState.Starting);
        Parts? parts = null;
        try
        {
            var refusal = CheckPreconditions(request.Keyboard);
            if (refusal is null && cancel.IsCancellationRequested)
            {
                refusal = PendingStop();
            }
            if (refusal is not null)
            {
                return End(refusal);
            }
            parts = new Parts(request, _services.SwallowInjected);
            Volatile.Write(ref _parts, parts);
            StartParts(parts, cancel);
            return null;
        }
        catch (Exception e)
        {
            var end = e switch
            {
                OperationCanceledException => PendingStop(),
                PadException pad => new SessionEnd(EndReason.PadFailed, pad.Message),
                _ => new SessionEnd(EndReason.StartFailed, "The session could not start: " + e.Message),
            };
            if (parts is null)
            {
                return End(end);
            }
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

        // Keyboards first, so an unplug while the rest starts is not missed. The engine is
        // created paused and goes live only once start has checked the board.
        parts.OnKeyboards = _ => Post(() => OnKeyboards(parts, removal: false));
        _services.Keyboards.Changed += parts.OnKeyboards;
        parts.OnRemoving = container =>
        {
            // Pump thread: volatile writes now, the state change on the session thread.
            // Another board's removal is not this session's business; an unknown one might be.
            if (container is { } removed && removed != request.Keyboard)
            {
                return;
            }
            Volatile.Write(ref parts.KeyboardRemoved, true);
            Volatile.Read(ref parts.Engine)?.Paused = true;
            Post(() => OnKeyboards(parts, removal: true));
        };
        _services.Keyboards.Removing += parts.OnRemoving;

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

        var engine = new EngineLoop(parts.Mapper, parts.Snapshot, parts.Pad!, parts.Flag) { Paused = true };
        engine.Faulted += reason => RequestStop(parts, new SessionEnd(EndReason.EngineFault, reason));
        Volatile.Write(ref parts.Engine, engine);
        engine.Start();

        parts.CrashGuard = new CrashGuard(() => Emergency(parts));
        LookForGame(parts);
        if (Volatile.Read(ref parts.Game) is null)
        {
            parts.GameSearch = new Timer(_ => Post(() => LookForGame(parts)), null, GameSearchMs, GameSearchMs);
        }
        parts.OnPower = () => RequestStop(parts, SessionEnd.For(EndReason.SleepOrWake));
        _services.Power.SleepOrWake += parts.OnPower;
        cancel.ThrowIfCancellationRequested();

        var watchdog = new Watchdog(
            () => engine.LastTickTicks,
            () => engine.PadPresent,
            () => Volatile.Read(ref parts.Hook)?.LastEventTime ?? 0,
            () => _services.RawInput.LastEventTime,
            () => parts.Flag.IsGameForeground);
        watchdog.Arm(Stopwatch.GetTimestamp());
        Volatile.Write(ref parts.Watchdog, watchdog);
        parts.Health = new Timer(_ => Post(() => CheckHealth(parts)), null, HealthCheckMs, HealthCheckMs);

        // Live only if the board is listed and no removal came in while starting. The
        // debounced list can still show a board that just went away. A removal racing this
        // line also posted OnKeyboards, which runs next and pauses.
        engine.Paused = Volatile.Read(ref parts.KeyboardRemoved) || !KeyboardPresent(request.Keyboard);
        SetState(engine.Paused ? SessionState.Paused : SessionState.Running);
    }

    private void InstallHook(Parts parts)
    {
        var hook = NewHook(parts);
        Volatile.Write(ref parts.Hook, hook);
        hook.Start();
    }

    /// <summary>The session's hook, not yet started, with the stop hotkey and the watchdog's timer wired. Every hook the session installs comes from here.</summary>
    private KeyboardHook NewHook(Parts parts) => new(parts.Store, parts.Policy, parts.Flag, parts.MappedKeys)
    {
        Detached = _services.DetachedHook,
        StopRequested = () => RequestStop(parts, SessionEnd.For(EndReason.Hotkey)),
        Timer = () => OnHookTimer(parts),
    };

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
                // The stop goes first: nothing below can keep it from being asked for.
                RequestStop(parts, SessionEnd.For(EndReason.EngineStalled));
                Volatile.Read(ref parts.Pad)?.BeginUnplug();
                // From the hook thread this returns at once; the hook goes when this timer message returns.
                Volatile.Read(ref parts.Hook)?.Stop();
                break;
            case WatchdogVerdict.ControllerLost:
                RequestStop(parts, SessionEnd.For(EndReason.ControllerDisconnected));
                break;
            case WatchdogVerdict.HookLost:
                Post(() => ReinstallHook(parts));
                break;
        }
    }

    /// <summary>
    /// Session thread. Once the game's process is found, its exit stops the session.
    /// Called at start and, until then, every <see cref="GameSearchMs"/>, so Start works
    /// before the game is launched as well as after. After an exit the search runs again
    /// for <see cref="SessionServices.GameRelaunchGrace"/>: a game whose launcher starts a second copy
    /// of itself and exits keeps its session.
    /// </summary>
    private void LookForGame(Parts parts)
    {
        if (Volatile.Read(ref parts.Game) is not null || !ReferenceEquals(Volatile.Read(ref _parts), parts) || Volatile.Read(ref parts.StopRequested))
        {
            return;
        }
        var game = _services.FindGame(parts.Request.GamePath);
        if (game is null)
        {
            if (parts.GameGone is { } gone && gone.Elapsed >= _services.GameRelaunchGrace)
            {
                DoStop(parts, SessionEnd.For(EndReason.GameExited));
            }
            return;
        }
        Volatile.Write(ref parts.Game, game);
        parts.GameGone = null;
        parts.GameSearch?.Dispose();
        parts.GameSearch = null;
        game.OnExit(() => Post(() => OnGameExited(parts, game)));
    }

    /// <summary>Session thread. The watched game ended: look for it again before stopping.</summary>
    private void OnGameExited(Parts parts, IGameProcess game)
    {
        if (!IsCurrent(parts) || !ReferenceEquals(Volatile.Read(ref parts.Game), game))
        {
            return;
        }
        game.Dispose();
        Volatile.Write(ref parts.Game, null);
        parts.GameGone = Stopwatch.StartNew();
        parts.GameSearch = new Timer(_ => Post(() => LookForGame(parts)), null, GameSearchMs, GameSearchMs);
    }

    /// <summary>
    /// Session thread. A removal (already paused on the pump thread) pauses the session;
    /// the debounced list resumes it when the selected keyboard is there, gating what is
    /// unknown first, or keeps it paused when it is not. A removal whose board the pump
    /// could not name costs a pause of about the debounce and a gate on the held keys.
    /// </summary>
    private void OnKeyboards(Parts parts, bool removal)
    {
        if (!IsCurrent(parts))
        {
            return;
        }
        var engine = parts.Engine!;
        if (removal || !KeyboardPresent(parts.Request.Keyboard))
        {
            engine.Paused = true;
            if (State == SessionState.Running)
            {
                SetState(SessionState.Paused);
            }
            return;
        }
        if (State == SessionState.Paused || engine.Paused)
        {
            parts.Store.GateUnknown();
            engine.Paused = false;
            if (State == SessionState.Paused)
            {
                SetState(SessionState.Running);
            }
        }
    }

    /// <summary>
    /// Session thread, every <see cref="HealthCheckMs"/>. The watchdog lives on the hook
    /// thread's timer, so a hook thread that ended takes it along: that hook is replaced.
    /// A watchdog that throws on every tick, a tracker that stopped following focus, or a
    /// Raw Input pump that stopped (the unplug pause and hook-loss detection need it)
    /// ends the session.
    /// </summary>
    private void CheckHealth(Parts parts)
    {
        if (!IsCurrent(parts))
        {
            return;
        }
        var hook = Volatile.Read(ref parts.Hook);
        if (hook is not null && !hook.IsInstalled)
        {
            ReinstallHook(parts);
            return;
        }
        var faults = hook?.HandlerFaults ?? 0;
        if (faults - parts.HookFaultsSeen >= HealthFaultLimit)
        {
            DoStop(parts, new SessionEnd(EndReason.SafetyFault, SessionEnd.For(EndReason.SafetyFault).Message + " The keyboard hook's checks kept failing."));
            return;
        }
        parts.HookFaultsSeen = faults;
        if (Volatile.Read(ref parts.Foreground) is { IsRunning: false })
        {
            DoStop(parts, new SessionEnd(EndReason.SafetyFault, SessionEnd.For(EndReason.SafetyFault).Message + " Focus tracking stopped."));
            return;
        }
        if (!_services.RawInput.IsRunning)
        {
            DoStop(parts, new SessionEnd(EndReason.SafetyFault, SessionEnd.For(EndReason.SafetyFault).Message + " Keyboard device tracking stopped."));
        }
    }

    /// <summary>Session thread. Windows removed the hook, or its thread ended: gate what is unknown, put a new hook in, and count it.</summary>
    private void ReinstallHook(Parts parts)
    {
        if (!IsCurrent(parts))
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
            var hook = NewHook(parts);
            Volatile.Write(ref parts.Hook, hook);
            parts.HookFaultsSeen = 0;
            parts.Watchdog!.HookReinstalled();
            hook.Start();
            Interlocked.Increment(ref parts.HookReinstalls);
        }
        catch (Exception e)
        {
            DoStop(parts, new SessionEnd(EndReason.HookFailed, SessionEnd.For(EndReason.HookFailed).Message + " " + e.Message));
        }
    }

    /// <summary>The running session, not yet asked to stop.</summary>
    private bool IsCurrent(Parts parts) =>
        ReferenceEquals(Volatile.Read(ref _parts), parts)
        && State is SessionState.Running or SessionState.Paused
        && !Volatile.Read(ref parts.StopRequested);

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
    /// wrong comes back as one sentence for the status card, or null. A part whose
    /// thread did not stop is not disposed, so nothing joins it a second time.
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
            parts.Health?.Dispose();
            parts.CrashGuard?.Dispose();
            if (parts.OnPower is not null)
            {
                _services.Power.SleepOrWake -= parts.OnPower;
            }
            if (parts.OnKeyboards is not null)
            {
                _services.Keyboards.Changed -= parts.OnKeyboards;
            }
            if (parts.OnRemoving is not null)
            {
                _services.Keyboards.Removing -= parts.OnRemoving;
            }
            parts.GameSearch?.Dispose();
            Volatile.Read(ref parts.Game)?.Dispose();
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
            if (!unplugged)
            {
                // Only the process ending clears a controller the driver will not remove.
                Volatile.Write(ref _restartRequired, 1);
                return "The virtual controller could not be removed. Restart the app to clear it.";
            }
            return pad.UnplugError;
        });
        Step(() =>
        {
            if (Volatile.Read(ref parts.Hook) is not { } hook)
            {
                return null;
            }
            var stopped = hook.Stop();
            if (stopped)
            {
                hook.Dispose();
            }
            return stopped ? null : "The keyboard hook thread did not stop.";
        });
        Step(() =>
        {
            if (parts.Poller is not { } poller)
            {
                return null;
            }
            var stopped = poller.Stop();
            if (stopped)
            {
                poller.Dispose();
            }
            return stopped ? null : "The sensor thread did not stop.";
        });
        Step(() =>
        {
            if (Volatile.Read(ref parts.Foreground) is not { } foreground)
            {
                return null;
            }
            var stopped = foreground.Stop();
            if (stopped)
            {
                foreground.Dispose();
            }
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
        public IGameProcess? Game;
        public Timer? GameSearch;
        public Stopwatch? GameGone;
        public bool StopRequested;
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
        public Timer? Health;
        public Action? OnPower;
        public Action<IReadOnlyList<KeyboardInfo>>? OnKeyboards;
        public Action<Guid?>? OnRemoving;

        /// <summary>The board went away at some point since start began. Read once, at the end of start.</summary>
        public bool KeyboardRemoved;
        public int HookFaultsSeen;
        public int HookReinstalls;

        public Parts(SessionRequest request, bool swallowInjected)
        {
            Request = request;
            Policy = new HookPolicy { SwallowInjected = swallowInjected };
            Mapper = new Mapper(request.Profile, Store);
            MappedKeys = request.Profile.Keys.Select(k => k.Key.Key)
                .Concat(request.Profile.Axes.SelectMany(a => new[] { a.Negative.Key, a.Positive.Key }))
                .Distinct()
                .ToArray();
        }
    }
}
