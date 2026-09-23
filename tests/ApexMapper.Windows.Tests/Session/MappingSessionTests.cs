using System.Diagnostics;
using System.Runtime;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
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
/// the stage 3 ledger's findings. The hook runs its real thread, timer and callback body
/// but detached: nothing is installed into Windows and the real keyboard is never read,
/// so these tests neither swallow nor see anyone's keys. Key events arrive through the
/// callback body by hand. The pad, foreground, game, power, Raw Input, keyboard list and
/// vendor stream are fakes the test moves by hand.
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
    private readonly FakeRawInput _rawInput = new();
    private FakeForeground? _foreground;
    private DriverState _driverState = DriverState.Running;
    private bool _driverThrows;
    private bool _gameRunning = true;
    private ManualResetEventSlim? _connectGate;
    private Func<ForegroundFlag, IForegroundSource>? _createForeground;
    private Func<IVendorStream?> _openSensor = () => SlowStream();

    public MappingSessionTests()
    {
        _keyboards = new KeyboardDiscovery(() => [.. _boards]);
        _keyboards.Refresh();
        _session = new MappingSession(new SessionServices
        {
            Keyboards = _keyboards,
            RawInput = _rawInput,
            Power = _power,
            DriverState = () => _driverThrows ? throw new InvalidOperationException("service manager unavailable") : _driverState,
            ConnectPad = cancel =>
            {
                _connectGate?.Wait(cancel);
                return VirtualPad.Connect(() => _driver, cancel);
            },
            FindGame = _ => NextGame(),
            GameRelaunchGrace = TimeSpan.FromMilliseconds(300),
            CreateForeground = flag => _createForeground is { } create ? create(flag) : _foreground = new FakeForeground(flag),
            CreatePoller = (_, snapshot, config) => new SensorPoller(_openSensor, snapshot, config),
            // The game is "in front" in these tests; a real hook would swallow the keys of
            // whoever is using this PC and let their key state into the results.
            DetachedHook = true,
        });
    }

    public void Dispose()
    {
        _release.Set();
        _rawInput.Release();
        _connectGate?.Set();
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
        Assert.True(_session.Hook!.Detached, "a session hook in these tests must never reach the real keyboard");
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

    /// <summary>Holds the session thread inside its next health check until <see cref="FakeRawInput.Release"/>.</summary>
    private void HoldTheSessionThread()
    {
        _rawInput.Hold();
        Eventually(() => _rawInput.Held, "the session thread to be held", MappingSession.HealthCheckMs * 8);
    }

    /// <summary>An OS event time later than any real key event on this machine, as Raw Input would report one the hook never saw.</summary>
    private static uint FutureEventTime(int aheadMs) => (uint)(Environment.TickCount + aheadMs);

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
        Assert.Equal(0, _keyboards.SubscriberCount);
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
        _gameRunning = false;
        Game.Exit();
        Eventually(() => _session.State == SessionState.Idle, "the game's exit to stop the session", 5000);
        Assert.Equal(EndReason.GameExited, _session.LastEnd!.Reason);
    }

    [Fact]
    public async Task A_game_that_relaunches_itself_within_the_grace_keeps_its_session()
    {
        await StartRunning();

        Game.Exit();

        Eventually(() => _games.Count == 2 && _session.Status().GameRunning, "the relaunched game to be found", 5000);
        Thread.Sleep(500);
        Assert.Equal(SessionState.Running, _session.State);
        Assert.True(_games[0].Disposed);
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
    public async Task The_status_carries_the_sensor_cycle_period_while_a_session_runs()
    {
        Assert.True(float.IsNaN(_session.Status().CycleP50Ms));

        await StartRunning();
        Eventually(() => _session.Status() is { CycleP50Ms: > 0f, CycleP99Ms: > 0f }, "the sensor cycles to be timed");

        await _session.StopAsync(EndReason.UserStop);
        Assert.True(float.IsNaN(_session.Status().CycleP99Ms));
    }

    /// <summary>The held captures, which have W at 4095, one read a millisecond.</summary>
    private static FakeVendorStream HeldStream() => new()
    {
        OnRead = (_, command, selector) =>
        {
            Thread.Sleep(1);
            return command == SensorRequest.GroupCommand ? Fixtures.HeldGroup(selector) : FakeVendorStream.DefaultReply(command, selector);
        },
    };

    [Fact]
    public async Task A_key_calibrated_short_of_the_sensor_s_limit_is_reported_while_it_reads_at_it()
    {
        // The fixtures calibrate W 2000 counts above rest.
        _openSensor = HeldStream;

        await StartRunning();

        Eventually(() => _session.Status().KeysAtLimit is [var key] && key == DefaultProfiles.Key.W, "W to be reported at the sensor's limit");
    }

    [Fact]
    public async Task A_key_calibrated_at_the_sensor_s_limit_is_not_reported_there()
    {
        _openSensor = HeldStream;
        var calibrations = new Dictionary<ScanCode, KeyCalibration>(Calibrations());
        var w = calibrations[W];
        calibrations[W] = KeyCalibration.Create(w.Rest, KeyCalibration.MaxCount, w.NoiseBand, w.SensorIndex);
        var profile = CompiledProfile.TryCompile(DefaultProfiles.Forza(), SensorMap.Default, calibrations, out _)!;

        Assert.Null(await _session.StartAsync(new SessionRequest(KeyboardId, GamePath, profile, Fixtures.Signatures(2, 3))));
        Assert.True(_session.Hook!.Detached, "a session hook in these tests must never reach the real keyboard");
        Eventually(() => _session.Status() is { CycleP50Ms: > 0f }, "the sensor cycles to be timed");

        for (var i = 0; i < 20; i++)
        {
            Assert.Empty(_session.Status().KeysAtLimit ?? []);
            Thread.Sleep(5);
        }
    }

    [Fact]
    public async Task Losing_focus_zeroes_the_pad_and_hands_held_keys_back_and_the_session_keeps_running()
    {
        await RunningWithSpaceHeld();

        _foreground!.Lose();

        WaitForPad(PadReport.Neutral);
        // With the flag down this passes either way; the regain test below pins ForegroundLost.
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

    /// <summary>The session thread is held, so only the pump-thread write can zero the pad.</summary>
    [Theory]
    [InlineData("this board")]
    [InlineData("unknown board")]
    public async Task A_removal_of_this_or_an_unknown_board_pauses_at_once_and_the_debounced_list_resumes_with_keys_gated(string which)
    {
        await RunningWithSpaceHeld();
        HoldTheSessionThread();

        // Pump thread: a keyboard went away. The debounced list, 500 ms later, still has the board (a replug).
        _keyboards.OnDeviceChanged(1, arrived: false, which == "this board" ? KeyboardId : null);

        WaitForPad(PadReport.Neutral);
        Assert.Equal(SessionState.Running, _session.State);
        _rawInput.Release();
        Eventually(() => _session.State == SessionState.Paused, "pause on removal");
        Eventually(() => _session.State == SessionState.Running, "resume from the debounced list", KeyboardDiscovery.DebounceMs * 4);
        StaysAt(PadReport.Neutral);
        Assert.True(_session.Status().KeysAwaitingRelease);
        Press(Space, down: false);
        Press(Space, down: true);
        WaitForPad(ButtonA);
    }

    /// <summary>
    /// The board goes away while the pad connects. The debounced list still has it when
    /// start finishes, and start must not take the list's word over the removal.
    /// </summary>
    [Fact]
    public async Task A_removal_during_start_brings_the_session_up_paused()
    {
        var states = new List<SessionState>();
        _session.StateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        _connectGate = new ManualResetEventSlim(false);
        var start = _session.StartAsync(Request());
        Eventually(() => _keyboards.SubscriberCount > 0, "the session to follow the keyboards before the pad connects");

        _boards.Clear();
        _keyboards.OnDeviceChanged(1, arrived: false, KeyboardId);
        _connectGate.Set();

        Assert.Null(await start);
        Eventually(() => _keyboards.Current.Count == 0, "the debounced list", KeyboardDiscovery.DebounceMs * 4);
        _foreground!.Gain();
        Press(Space, down: true);
        StaysAt(PadReport.Neutral);
        lock (states)
        {
            Assert.Equal([SessionState.Starting, SessionState.Paused], states);
        }

        Press(Space, down: false);
        _boards.Add(Board);
        _keyboards.Refresh();
        Eventually(() => _session.State == SessionState.Running, "resume on replug");
    }

    [Fact]
    public async Task Another_board_going_away_leaves_the_session_alone()
    {
        await RunningWithSpaceHeld();

        _keyboards.OnDeviceChanged(1, arrived: false, new Guid("11111111-2222-3333-4444-555555555555"));

        StaysAt(ButtonA);
        Thread.Sleep(KeyboardDiscovery.DebounceMs + 200);
        Assert.Equal(SessionState.Running, _session.State);
        Assert.False(_session.Status().KeysAwaitingRelease);
        Assert.Equal(ButtonA, _driver.State);
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
                _gameRunning = false;
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
        Assert.DoesNotContain("apex-hook", _driver.ReadBackThreads);
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
        _driverThrows = true;

        var end = await _session.StartAsync(Request());

        Assert.Equal(EndReason.StartFailed, end?.Reason);
        Assert.Contains("service manager unavailable", end!.Message);
        Assert.Equal(SessionState.Idle, _session.State);
        _driverThrows = false;
        await StartRunning();
    }

    [Fact]
    public async Task A_second_start_while_one_is_in_flight_or_running_is_refused()
    {
        _connectGate = new ManualResetEventSlim(false);

        var first = _session.StartAsync(Request());
        var second = await _session.StartAsync(Request());

        Assert.Equal(EndReason.StartFailed, second?.Reason);
        Assert.Contains("in progress", second!.Message);
        _connectGate.Set();
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

    /// <summary>
    /// Health checks queue up behind a held session thread while the watchdog removes the
    /// hook for a stall. When they run, before the stop does, none may put a hook back.
    /// </summary>
    [Fact]
    public async Task A_health_check_queued_behind_a_stall_never_puts_the_hook_back()
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
        HoldTheSessionThread();

        Press(Space, down: false);
        Eventually(() => !hook.IsInstalled && !_driver.Connected, "the watchdog to unplug the pad and remove the hook");
        Thread.Sleep(3 * MappingSession.HealthCheckMs);
        _rawInput.Release();

        // The queued checks have run; the stop is held at its start.
        Eventually(() => _session.State == SessionState.Stopping, "the stop to begin");
        Assert.Same(hook, _session.Hook);
        Assert.False(hook.IsInstalled);
        Assert.Equal(0, _session.Status().HookReinstalls);
        holdStop.Set();
        Eventually(() => _session.State == SessionState.Idle, "the session to stop");
        Assert.Equal(EndReason.EngineStalled, _session.LastEnd!.Reason);
    }

    [Fact]
    public async Task A_hook_windows_removed_is_noticed_and_put_back_with_the_held_key_gated()
    {
        await RunningWithSpaceHeld();
        var first = _session.Hook!;
        // Let the watchdog take its baseline for the focus it just saw.
        Thread.Sleep(3 * KeyboardHook.TimerMs);

        // Raw Input saw a key the hook never did, as when Windows drops the hook. Real key
        // events on this machine are all older than this one, so typing cannot hide it.
        _rawInput.LastEventTime = FutureEventTime(60_000);

        Eventually(() => !ReferenceEquals(_session.Hook, first), "a new hook");
        Eventually(() => _session.Status().HookReinstalls == 1 && _session.Hook!.IsInstalled, "the new hook installed");
        Assert.False(first.IsInstalled);
        Assert.True(_session.Hook!.Detached, "a reinstalled hook comes from the same factory, detached in tests");
        Assert.Equal(SessionState.Running, _session.State);
        Assert.True(_session.Store!.IsGated(Space.Slot));
        WaitForPad(PadReport.Neutral);
        Press(Space, down: false);
        Assert.True(Press(Space, down: true));
        WaitForPad(ButtonA);
        StaysAt(ButtonA);
        Assert.Equal(1, _session.Status().HookReinstalls);

        // The new hook is judged from its own start: a second loss is noticed too.
        var second = _session.Hook!;
        Thread.Sleep(3 * KeyboardHook.TimerMs);
        _rawInput.LastEventTime = FutureEventTime(120_000);
        Eventually(() => _session.Status().HookReinstalls == 2 && !ReferenceEquals(_session.Hook, second), "a second reinstall");

        // A reinstalled hook still carries the stop hotkey.
        _session.Hook!.StopRequested!();
        Eventually(() => _session.State == SessionState.Idle, "the hotkey to stop the session");
        Assert.Equal(EndReason.Hotkey, _session.LastEnd!.Reason);
    }

    [Fact]
    public async Task A_hook_whose_thread_ended_is_replaced_by_the_health_check()
    {
        await StartRunning();
        var first = _session.Hook!;

        Assert.True(first.Stop());

        Eventually(() => _session.Status().HookReinstalls == 1 && _session.Hook is { IsInstalled: true }, "the health check to put a hook back", 5000);
        Assert.NotSame(first, _session.Hook);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public async Task A_tracker_whose_thread_ended_stops_the_session()
    {
        await StartRunning();

        _foreground!.Died = true;

        Eventually(() => _session.State == SessionState.Idle, "the health check to stop the session", 5000);
        Assert.Equal(EndReason.SafetyFault, _session.LastEnd!.Reason);
        Assert.Contains("Focus tracking stopped", _session.LastEnd.Message);
    }

    [Fact]
    public async Task A_raw_input_pump_that_stopped_stops_the_session()
    {
        await StartRunning();

        _rawInput.Running = false;

        Eventually(() => _session.State == SessionState.Idle, "the health check to stop the session", 5000);
        Assert.Equal(EndReason.SafetyFault, _session.LastEnd!.Reason);
        Assert.Contains("Keyboard device tracking stopped", _session.LastEnd.Message);
    }

    [Fact]
    public async Task A_watchdog_that_throws_on_every_tick_stops_the_session()
    {
        await StartRunning();
        _foreground!.Gain();

        _rawInput.Throws = true;

        Eventually(() => _session.State == SessionState.Idle, "the health check to stop the session", 5000);
        Assert.Equal(EndReason.SafetyFault, _session.LastEnd!.Reason);
        Assert.Contains("kept failing", _session.LastEnd.Message);
    }

    [Fact]
    public async Task A_stale_event_from_an_earlier_session_never_stops_the_next_one()
    {
        await StartRunning();
        var earlier = Game;
        await _session.StopAsync(EndReason.UserStop);
        await StartRunning();

        earlier.Exit();

        // Anything the stale exit posted runs before this start, which the running session refuses.
        Assert.Equal(EndReason.StartFailed, (await _session.StartAsync(Request()))?.Reason);
        Thread.Sleep(2 * MappingSession.GameSearchMs);
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

    /// <summary>
    /// The session thread is held, so what happens is the guard's work, not a teardown the
    /// watchdog set off. The app's crash handler runs it first through CrashStop, then the
    /// guard's own handler runs it again.
    /// </summary>
    [Fact]
    public async Task The_crash_guard_zeros_and_unplugs_the_pad_removes_the_hook_and_restores_the_gc_mode()
    {
        await RunningWithSpaceHeld();
        var hook = _session.Hook!;
        HoldTheSessionThread();

        _session.CrashStop();
        _session.CrashGuard!.Run();

        var log = _driver.Log;
        Assert.Equal(["submit neutral", "disconnect"], log.Skip(log.Count - 2));
        Assert.Single(log, entry => entry == "disconnect");
        Assert.False(hook.IsInstalled);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
        Assert.Equal(SessionState.Running, _session.State);
    }
}
