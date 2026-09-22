using System.Diagnostics;
using System.Runtime;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;
using ApexMapper.Windows.Tests.Hid;
using ApexMapper.Windows.Tests.Output;
using Nefarius.ViGEm.Client.Exceptions;
using Xunit;
using static ApexMapper.Windows.Tests.Session.SessionFixtures;

namespace ApexMapper.Windows.Tests.Session;

/// <summary>
/// The design's session table, one test per row, plus the orderings the plan names and
/// the stage 3 ledger's findings. The hook is real (installed in this process, and it
/// swallows mapped physical keys while a test holds the game in front); the pad,
/// foreground, game, power, keyboard list and vendor stream are fakes the test moves by
/// hand.
/// </summary>
[Collection(ProcessSingletons.Name)]
public sealed class MappingSessionTests : IDisposable
{
    private static readonly PadReport ButtonA = PadReport.Neutral with { Buttons = PadTarget.ButtonA.ButtonBit() };

    private readonly List<KeyboardInfo> _boards = [Board];
    private readonly KeyboardDiscovery _keyboards;
    private readonly FakePower _power = new();
    private readonly List<FakeGame> _games = [];
    private readonly FakePadDriver _driver = new();
    private readonly MappingSession _session;
    private readonly GCLatencyMode _latencyBefore = GCSettings.LatencyMode;
    private readonly ManualResetEventSlim _release = new(false);
    private FakeForeground? _foreground;
    private DriverState _driverState = DriverState.Running;
    private bool _gameRunning = true;
    private Func<string, IGameProcess?>? _findGame;
    private Func<ForegroundFlag, IForegroundSource>? _createForeground;
    private long _rawEvents;
    private Func<IVendorStream?> _openSensor = () => SlowStream();

    public MappingSessionTests()
    {
        _keyboards = new KeyboardDiscovery(() => [.. _boards]);
        _keyboards.Refresh();
        _session = new MappingSession(new SessionServices
        {
            Keyboards = _keyboards,
            RawInputEvents = () => Volatile.Read(ref _rawEvents),
            Power = _power,
            DriverState = () => _driverState,
            ConnectPad = cancel => VirtualPad.Connect(() => _driver, cancel),
            FindGame = path => _findGame is { } find ? find(path) : NextGame(),
            CreateForeground = flag => _createForeground is { } create ? create(flag) : _foreground = new FakeForeground(flag),
            CreatePoller = (_, snapshot, config) => new SensorPoller(_openSensor, snapshot, config),
        });
    }

    public void Dispose()
    {
        _release.Set();
        _session.Dispose();
        _keyboards.Dispose();
        _release.Dispose();
    }

    private FakeGame Game => _games[^1];

    private FakeGame? NextGame()
    {
        if (!_gameRunning)
        {
            return null;
        }
        var game = new FakeGame();
        _games.Add(game);
        return game;
    }

    private static void Eventually(Func<bool> condition, string what, int timeoutMs = 2000) =>
        Assert.True(SpinWait.SpinUntil(condition, timeoutMs), what);

    private async Task StartRunning()
    {
        Assert.Null(await _session.StartAsync(Request()));
        Assert.Equal(SessionState.Running, _session.State);
    }

    /// <summary>A key event through the real hook's callback body, as Windows would deliver it. True when swallowed.</summary>
    private bool Press(ScanCode key, bool down) => _session.Hook!.Handle(down ? User32.WM_KEYDOWN : User32.WM_KEYUP, Key(key));

    private static bool Press(KeyboardHook hook, ScanCode key, bool down) => hook.Handle(down ? User32.WM_KEYDOWN : User32.WM_KEYUP, Key(key));

    private void WaitForPad(PadReport expected) =>
        Eventually(() => _driver.State == expected, $"pad to read {FakePadDriver.Describe(expected)}, was {FakePadDriver.Describe(_driver.State)}");

    /// <summary>The pad stays at a report for a while, long enough for the engine to have changed it.</summary>
    private void StaysAt(PadReport expected)
    {
        Thread.Sleep(50);
        Assert.Equal(expected, _driver.State);
    }

    /// <summary>Game in front, Space held and on the pad.</summary>
    private async Task RunningWithSpaceHeld()
    {
        await StartRunning();
        _foreground!.Gain();
        Assert.True(Press(Space, down: true));
        WaitForPad(ButtonA);
    }

    /// <summary>Blocks the engine's next driver submit until the test ends: an engine stuck inside the driver.</summary>
    private void WedgeTheEngine()
    {
        _driver.OnSubmit = _ =>
        {
            if (Thread.CurrentThread.Name == "apex-engine")
            {
                _release.Wait(TestContext.Current.CancellationToken);
            }
        };
    }

    private void AssertStoppedCleanly(EndReason reason)
    {
        Eventually(() => _session.State == SessionState.Idle && _session.LastEnd is not null, "session to stop");
        Assert.Equal(reason, _session.LastEnd!.Reason);
        var log = _driver.Log;
        Assert.Equal(["submit neutral", "disconnect", "dispose"], log.Skip(log.Count - 3));
        Assert.True(_foreground!.Stopped);
        Assert.True(Game.Disposed);
        Assert.Equal(0, _power.Subscribers);
        Assert.Null(_session.Hook);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
        Assert.Equal(0, _session.HandlerFaults);
    }

    [Theory]
    [InlineData("keyboard", EndReason.KeyboardMissing)]
    [InlineData("driver missing", EndReason.DriverMissing)]
    [InlineData("driver not started", EndReason.DriverNotStarted)]
    public async Task Start_is_refused_when_a_precondition_fails_and_touches_nothing(string missing, EndReason reason)
    {
        switch (missing)
        {
            case "keyboard":
                _boards.Clear();
                _keyboards.Refresh();
                break;
            case "driver missing":
                _driverState = DriverState.Missing;
                break;
            case "driver not started":
                _driverState = DriverState.NotStarted;
                break;
        }

        Assert.Equal(reason, _session.WhyNotStartable(KeyboardId)?.Reason);
        var end = await _session.StartAsync(Request());

        Assert.Equal(reason, end?.Reason);
        Assert.Equal(end, _session.LastEnd);
        Assert.Equal(SessionState.Idle, _session.State);
        Assert.Empty(_driver.Log);
        Assert.Null(_foreground);
    }

    [Fact]
    public async Task Start_before_the_game_runs_waits_for_it_and_stops_when_it_exits()
    {
        _gameRunning = false;

        await StartRunning();

        Assert.False(_session.Status().GameRunning);
        Assert.True(_session.Hook!.IsInstalled);
        _gameRunning = true;
        Eventually(() => _session.Status().GameRunning, "the session to find the game", MappingSession.GameSearchMs * 3);
        Assert.Single(_games);
        Game.Exit();
        Eventually(() => _session.State == SessionState.Idle, "the game's exit to stop the session");
        Assert.Equal(EndReason.GameExited, _session.LastEnd!.Reason);
    }

    [Fact]
    public async Task An_unknown_driver_state_does_not_refuse_the_start()
    {
        _driverState = DriverState.Unknown;

        await StartRunning();
    }

    [Fact]
    public async Task Start_connects_the_pad_at_neutral_installs_the_hook_and_gates_the_analog_keys()
    {
        // A sensor that never answers: no reading clears the start gate.
        _openSensor = () => new FakeVendorStream { OnRead = (_, _, _) => { Thread.Sleep(5); return null; } };
        Assert.Null(_session.WhyNotStartable(KeyboardId));

        await StartRunning();

        Assert.Equal(["connect", "submit LX=1 RT=0 LT=0 B=0", "submit neutral"], _driver.Log.Take(3));
        Assert.True(_session.Hook!.IsInstalled);
        Assert.True(_foreground!.Started);
        Assert.Equal(GamePath, _foreground.GamePath);
        foreach (var key in Calibrations().Keys)
        {
            Assert.True(_session.Store!.IsGated(key.Slot), $"{key} gated at start");
        }
        Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);

        await _session.StopAsync(EndReason.UserStop);
        AssertStoppedCleanly(EndReason.UserStop);
    }

    [Fact]
    public async Task Losing_focus_zeroes_the_pad_and_hands_held_keys_back_and_the_session_keeps_running()
    {
        await RunningWithSpaceHeld();

        _foreground!.Lose();

        WaitForPad(PadReport.Neutral);
        Assert.False(Press(Space, down: false), "the key-up of a key held through the loss reaches the desktop");
        Assert.Equal(SessionState.Running, _session.State);
        Assert.False(_session.Status().GameHasFocus);
        Assert.True(_session.Status().SubmitCount > 0);
    }

    [Fact]
    public async Task Regaining_focus_resumes_mapping_but_a_key_held_through_it_stays_dead_until_released()
    {
        await RunningWithSpaceHeld();
        _foreground!.Lose();
        WaitForPad(PadReport.Neutral);

        _foreground.Gain();

        StaysAt(PadReport.Neutral);
        Assert.True(_session.Status().KeysAwaitingRelease);
        Assert.False(Press(Space, down: false), "handed back on the loss, so its key-up goes where its down went");
        Assert.False(_session.Status().KeysAwaitingRelease);
        Assert.True(Press(Space, down: true));
        WaitForPad(ButtonA);
    }

    [Fact]
    public async Task A_dead_sensor_falls_back_to_the_hook_with_the_reason_shown()
    {
        _openSensor = () => new FakeVendorStream { OnRead = (_, _, _) => { Thread.Sleep(5); return null; } };
        await StartRunning();
        _foreground!.Gain();

        Assert.True(Press(W, down: true));

        WaitForPad(PadReport.Neutral with { RightTrigger = 255 });
        var status = _session.Status();
        Assert.Equal(4, status.FallbackKeys);
        Assert.Contains("did not answer", status.SensorProblem);
    }

    [Fact]
    public async Task Unplugging_the_keyboard_pauses_with_the_pad_neutral_and_keys_still_blocked_and_replugging_resumes()
    {
        await RunningWithSpaceHeld();

        _boards.Clear();
        _keyboards.Refresh();

        Eventually(() => _session.State == SessionState.Paused, "pause on unplug");
        WaitForPad(PadReport.Neutral);
        Assert.True(Press(Space, down: true), "a mapped key is still swallowed while paused");

        _boards.Add(Board);
        _keyboards.Refresh();

        Eventually(() => _session.State == SessionState.Running, "resume on replug");
        StaysAt(PadReport.Neutral);
        Assert.True(_session.Status().KeysAwaitingRelease);
        Press(Space, down: false);
        Press(Space, down: true);
        WaitForPad(ButtonA);
    }

    [Fact]
    public async Task A_keyboard_removal_pauses_at_once_and_the_debounced_list_resumes_with_keys_gated()
    {
        await RunningWithSpaceHeld();
        var clock = Stopwatch.StartNew();

        // Pump thread: a keyboard went away. The debounced list, 500 ms later, still has the board (a replug, or another keyboard).
        _keyboards.OnDeviceChanged(1, arrived: false);

        WaitForPad(PadReport.Neutral);
        Assert.True(clock.ElapsedMilliseconds < KeyboardDiscovery.DebounceMs / 2, $"neutral after {clock.ElapsedMilliseconds} ms");
        Eventually(() => _session.State == SessionState.Paused, "pause on removal");
        Eventually(() => _session.State == SessionState.Running, "resume from the debounced list", KeyboardDiscovery.DebounceMs * 4);
        StaysAt(PadReport.Neutral);
        Assert.True(_session.Status().KeysAwaitingRelease);
        Press(Space, down: false);
        Press(Space, down: true);
        WaitForPad(ButtonA);
    }

    [Theory]
    [InlineData(EndReason.Hotkey)]
    [InlineData(EndReason.GameExited)]
    [InlineData(EndReason.ControllerDisconnected)]
    [InlineData(EndReason.SleepOrWake)]
    [InlineData(EndReason.UserStop)]
    [InlineData(EndReason.ProfileEdited)]
    [InlineData(EndReason.AppClosing)]
    public async Task Every_stop_zeroes_and_unplugs_the_pad_before_the_hook_goes(EndReason reason)
    {
        await RunningWithSpaceHeld();
        var hook = _session.Hook!;
        bool? hookInstalledAtZero = null;
        _driver.OnSubmit = r =>
        {
            if (r == PadReport.Neutral && Thread.CurrentThread.Name == "apex-unplug")
            {
                hookInstalledAtZero = hook.IsInstalled;
            }
        };

        Task stopped = Task.CompletedTask;
        switch (reason)
        {
            case EndReason.Hotkey:
                hook.StopRequested!();
                break;
            case EndReason.GameExited:
                Game.Exit();
                break;
            case EndReason.ControllerDisconnected:
                _driver.Gone = true;
                break;
            case EndReason.SleepOrWake:
                _power.Raise();
                break;
            case EndReason.AppClosing:
                stopped = Task.Run(_session.Dispose, TestContext.Current.CancellationToken);
                break;
            default:
                stopped = _session.StopAsync(reason);
                break;
        }

        await stopped;
        AssertStoppedCleanly(reason);
        Assert.True(hookInstalledAtZero);
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public async Task A_stop_during_start_cancels_the_pad_connect_and_unwinds()
    {
        FakeVendorStream? stream = null;
        _openSensor = () => stream = SlowStream();
        _driver.ReadBackOverride = PadReport.Neutral with { RightTrigger = 9 };

        var start = _session.StartAsync(Request());
        Eventually(() => _driver.Connected, "the pad connect to begin");
        _session.RequestStop(EndReason.UserStop);
        var end = await start;

        Assert.Equal(EndReason.UserStop, end?.Reason);
        Assert.Equal(SessionState.Idle, _session.State);
        Assert.False(_driver.Connected);
        Assert.True(_driver.Disposed);
        Assert.Null(_foreground);
        Assert.True(stream!.IsDisposed);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
    }

    [Fact]
    public async Task A_start_that_fails_after_the_pad_connected_unwinds_everything()
    {
        FakeVendorStream? stream = null;
        _openSensor = () => stream = SlowStream();
        _createForeground = _ => throw new InvalidOperationException("no foreground today");

        var end = await _session.StartAsync(Request());

        Assert.Equal(EndReason.StartFailed, end?.Reason);
        Assert.Contains("no foreground today", end!.Message);
        Assert.Equal(SessionState.Idle, _session.State);
        var log = _driver.Log;
        Assert.Equal(["submit neutral", "disconnect", "dispose"], log.Skip(log.Count - 3));
        Eventually(() => stream?.IsDisposed == true, "the sensor to stop");
        Assert.Empty(_games);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
    }

    [Fact]
    public async Task A_precondition_check_that_throws_fails_the_start_and_the_next_start_works()
    {
        _findGame = _ => throw new InvalidOperationException("process list unavailable");

        var end = await _session.StartAsync(Request());

        Assert.Equal(EndReason.StartFailed, end?.Reason);
        Assert.Equal(SessionState.Idle, _session.State);
        _findGame = null;
        await StartRunning();
    }

    [Fact]
    public async Task A_second_start_while_one_is_in_flight_or_running_is_refused()
    {
        _driver.UserIndexUnreportedFor = 20;

        var first = _session.StartAsync(Request());
        var second = await _session.StartAsync(Request());

        Assert.Equal(EndReason.StartFailed, second?.Reason);
        Assert.Contains("in progress", second!.Message);
        Assert.Null(await first);
        Assert.Equal(EndReason.StartFailed, (await _session.StartAsync(Request()))?.Reason);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Theory]
    [InlineData("backoff")]
    [InlineData("blocked read")]
    public async Task The_hotkey_stops_at_once_while_the_sensor_waits(string wait)
    {
        var reads = 0;
        _openSensor = wait == "backoff"
            ? () =>
            {
                Interlocked.Increment(ref reads);
                return null;
            }
            : () =>
            {
                var blocked = new FakeVendorStream();
                blocked.OnRead = (_, _, _) =>
                {
                    Interlocked.Increment(ref reads);
                    blocked.BlockUntilDisposed();
                    return null;
                };
                return blocked;
            };
        await StartRunning();
        Eventually(() => Volatile.Read(ref reads) > 0, "the sensor to reach its wait");
        var clock = Stopwatch.StartNew();

        _session.Hook!.StopRequested!();

        Eventually(() => _session.State == SessionState.Idle, "stop");
        // Under the sensor's 1 s backoff and its 2 s join: the stop woke it rather than outwaited it.
        Assert.True(clock.ElapsedMilliseconds < SensorPoller.BackoffMs - 100, $"Stop took {clock.ElapsedMilliseconds} ms with the sensor in a {wait}.");
        Assert.Equal(EndReason.Hotkey, _session.LastEnd!.Reason);
        Assert.DoesNotContain("did not stop", _session.LastEnd.Message);
    }

    [Fact]
    public async Task The_hotkey_raised_on_the_hook_thread_itself_stops_without_waiting_on_that_thread()
    {
        await StartRunning();
        var hook = _session.Hook!;
        var clock = Stopwatch.StartNew();

        // The hook's own timer raises the stop, as the real chord would from the callback.
        hook.Timer = hook.StopRequested;

        Eventually(() => _session.State == SessionState.Idle, "stop");
        Assert.True(clock.ElapsedMilliseconds < 1000, $"Stop took {clock.ElapsedMilliseconds} ms.");
        Assert.Equal(EndReason.Hotkey, _session.LastEnd!.Reason);
        Assert.DoesNotContain("did not stop", _session.LastEnd.Message);
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public async Task Unplugging_during_alt_tab_stays_paused_through_the_return_and_resumes_on_replug()
    {
        await StartRunning();
        _foreground!.Gain();
        _foreground.Lose();
        _boards.Clear();
        _keyboards.Refresh();
        Eventually(() => _session.State == SessionState.Paused, "pause on unplug");

        _foreground.Gain();
        Assert.True(Press(Space, down: true));

        StaysAt(PadReport.Neutral);
        Assert.Equal(SessionState.Paused, _session.State);

        _boards.Add(Board);
        _keyboards.Refresh();
        Eventually(() => _session.State == SessionState.Running, "resume on replug");
        StaysAt(PadReport.Neutral);
        Press(Space, down: false);
        Press(Space, down: true);
        WaitForPad(ButtonA);
    }

    /// <summary>
    /// The session thread is held at the start of its stop, as it would be in a wedged
    /// process, so only the watchdog's own actions on the hook thread can be at work.
    /// </summary>
    [Fact]
    public async Task A_stalled_engine_is_unplugged_by_the_watchdog_itself_and_its_late_return_never_reaches_the_driver()
    {
        using var holdStop = new ManualResetEventSlim(false);
        await RunningWithSpaceHeld();
        var hook = _session.Hook!;
        _session.StateChanged += state =>
        {
            if (state == SessionState.Stopping)
            {
                holdStop.Wait(TestContext.Current.CancellationToken);
            }
        };
        WedgeTheEngine();
        var clock = Stopwatch.StartNew();

        Press(Space, down: false);

        Eventually(() => !_driver.Connected, "the watchdog to unplug the pad");
        var unpluggedAfter = clock.ElapsedMilliseconds;
        Eventually(() => !hook.IsInstalled, "the watchdog to remove the hook");
        Assert.Equal(SessionState.Stopping, _session.State);
        var log = _driver.Log;
        Assert.Equal(["submit neutral", "disconnect"], log.Skip(log.Count - 2));

        holdStop.Set();
        Eventually(() => _session.State == SessionState.Idle, "the session to stop");
        _release.Set();
        Eventually(() => _driver.Log.Count == log.Count + 1, "the stuck submit to land");
        Thread.Sleep(50);

        Assert.InRange(unpluggedAfter, Watchdog.StallMs - 5, 1000);
        Assert.Equal(EndReason.EngineStalled, _session.LastEnd!.Reason);
        Assert.Contains("engine thread did not stop", _session.LastEnd.Message);
        Assert.Equal([.. log, FakePadDriver.Describe(PadReport.Neutral)], _driver.Log);
        Assert.False(hook.IsInstalled);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
    }

    [Fact]
    public async Task The_watchdog_stays_armed_while_paused()
    {
        await RunningWithSpaceHeld();
        WedgeTheEngine();

        // The pause changes the report to neutral, and that submit is the one that sticks.
        _boards.Clear();
        _keyboards.Refresh();

        Eventually(() => _session.State == SessionState.Idle, "the stall to stop the session", 3000);
        Assert.Equal(EndReason.EngineStalled, _session.LastEnd!.Reason);
    }

    [Fact]
    public async Task A_hook_windows_removed_is_noticed_and_put_back_with_the_held_key_gated()
    {
        await RunningWithSpaceHeld();
        var first = _session.Hook!;
        // Let the watchdog take its baseline for the focus it just saw.
        Thread.Sleep(3 * KeyboardHook.TimerMs);

        // Raw Input counts a key the hook never saw, as when Windows drops the hook.
        Interlocked.Increment(ref _rawEvents);

        Eventually(() => !ReferenceEquals(_session.Hook, first), "a new hook");
        Eventually(() => _session.Status().HookReinstalls == 1 && _session.Hook!.IsInstalled, "the new hook installed");
        Assert.False(first.IsInstalled);
        Assert.Equal(SessionState.Running, _session.State);
        Assert.True(_session.Store!.IsGated(Space.Slot));
        WaitForPad(PadReport.Neutral);
        Press(Space, down: false);
        Assert.True(Press(Space, down: true));
        WaitForPad(ButtonA);
        StaysAt(ButtonA);
        Assert.Equal(1, _session.Status().HookReinstalls);
    }

    [Fact]
    public async Task A_hook_whose_thread_ended_is_replaced_by_the_health_check()
    {
        await StartRunning();
        var first = _session.Hook!;

        Assert.True(first.Stop());

        Eventually(() => _session.Status().HookReinstalls == 1 && _session.Hook is { IsInstalled: true }, "the health check to put a hook back", MappingSession.HealthCheckMs * 4);
        Assert.NotSame(first, _session.Hook);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public async Task A_tracker_whose_thread_ended_stops_the_session()
    {
        await StartRunning();

        _foreground!.Died = true;

        Eventually(() => _session.State == SessionState.Idle, "the health check to stop the session", MappingSession.HealthCheckMs * 4);
        Assert.Equal(EndReason.SafetyFault, _session.LastEnd!.Reason);
        Assert.Contains("Focus tracking stopped", _session.LastEnd.Message);
    }

    [Fact]
    public async Task A_stale_event_from_an_earlier_session_never_stops_the_next_one()
    {
        await StartRunning();
        var earlier = Game;
        await _session.StopAsync(EndReason.UserStop);
        await StartRunning();

        earlier.Exit();

        StaysAt(PadReport.Neutral);
        Thread.Sleep(100);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public async Task A_driver_failure_on_the_engine_thread_stops_with_the_reason()
    {
        await StartRunning();
        _foreground!.Gain();
        _driver.OnSubmit = _ =>
        {
            if (Thread.CurrentThread.Name == "apex-engine")
            {
                throw new VigemTargetNotPluggedInException();
            }
        };

        Press(Space, down: true);

        Eventually(() => _session.State == SessionState.Idle, "the session to stop");
        Assert.Equal(EndReason.EngineFault, _session.LastEnd!.Reason);
        Assert.Contains("unplugged by the driver", _session.LastEnd.Message);
    }

    [Fact]
    public async Task A_hung_driver_bounds_the_stop_and_the_session_says_so()
    {
        await RunningWithSpaceHeld();
        Press(Space, down: false);
        _driver.OnSubmit = _ =>
        {
            if (Thread.CurrentThread.Name == "apex-unplug")
            {
                _release.Wait(TestContext.Current.CancellationToken);
            }
        };
        var hook = _session.Hook!;
        var clock = Stopwatch.StartNew();

        await _session.StopAsync(EndReason.UserStop);

        Assert.InRange(clock.ElapsedMilliseconds, VirtualPad.UnplugWaitMs - 50, VirtualPad.UnplugWaitMs + 1500);
        Assert.Equal(SessionState.Idle, _session.State);
        Assert.Contains("Restart the app", _session.LastEnd!.Message);
        Assert.False(hook.IsInstalled);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);

        // Everything else is shut down cleanly; only a restart clears the stuck controller.
        Assert.True(_session.RestartRequired);
        Assert.True(_session.Status().RestartRequired);
        Assert.Equal(EndReason.RestartRequired, _session.WhyNotStartable(KeyboardId)?.Reason);
        Assert.Equal(EndReason.RestartRequired, (await _session.StartAsync(Request()))?.Reason);
    }

    [Fact]
    public async Task The_crash_guard_zeros_and_unplugs_the_pad_removes_the_hook_and_restores_the_gc_mode()
    {
        await RunningWithSpaceHeld();
        var hook = _session.Hook!;

        _session.CrashGuard!.Run();

        var log = _driver.Log;
        Assert.Equal(["submit neutral", "disconnect"], log.Skip(log.Count - 2));
        Assert.False(hook.IsInstalled);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
    }
}
