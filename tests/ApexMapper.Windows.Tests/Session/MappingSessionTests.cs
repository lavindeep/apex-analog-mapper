using System.Diagnostics;
using System.Runtime;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
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
/// The design's session table, one test per row, plus the orderings the plan names.
/// The hook is real (installed in this process); the pad, foreground, game, power,
/// keyboard list and vendor stream are fakes the test moves by hand.
/// </summary>
[Collection(ProcessSingletons.Name)]
public sealed class MappingSessionTests : IDisposable
{
    private static readonly PadReport ButtonA = PadReport.Neutral with { Buttons = PadTarget.ButtonA.ButtonBit() };

    private readonly List<KeyboardInfo> _boards = [Board];
    private readonly KeyboardDiscovery _keyboards;
    private readonly FakePower _power = new();
    private readonly FakeGame _game = new();
    private readonly FakePadDriver _driver = new();
    private readonly MappingSession _session;
    private readonly GCLatencyMode _latencyBefore = GCSettings.LatencyMode;
    private FakeForeground? _foreground;
    private DriverState _driverState = DriverState.Running;
    private bool _gameRunning = true;
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
            FindGame = _ => _gameRunning ? _game : null,
            CreateForeground = flag => _foreground = new FakeForeground(flag),
            CreatePoller = (_, snapshot, config) => new SensorPoller(_openSensor, snapshot, config),
        });
    }

    public void Dispose()
    {
        _session.Dispose();
        _keyboards.Dispose();
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

    private void WaitForPad(PadReport expected) =>
        Eventually(() => _driver.State == expected, $"pad to read {FakePadDriver.Describe(expected)}, was {FakePadDriver.Describe(_driver.State)}");

    /// <summary>The pad stays at a report for a while, long enough for the engine to have changed it.</summary>
    private void StaysAt(PadReport expected)
    {
        Thread.Sleep(50);
        Assert.Equal(expected, _driver.State);
    }

    private void AssertStoppedCleanly(EndReason reason)
    {
        Eventually(() => _session.State == SessionState.Idle && _session.LastEnd is not null, "session to stop");
        Assert.Equal(reason, _session.LastEnd!.Reason);
        var log = _driver.Log;
        Assert.Equal(["submit neutral", "disconnect", "dispose"], log.Skip(log.Count - 3));
        Assert.True(_foreground!.Stopped);
        Assert.True(_game.Disposed);
        Assert.Equal(0, _power.Subscribers);
        Assert.Null(_session.Hook);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
        Assert.Equal(0, _session.HandlerFaults);
    }

    [Theory]
    [InlineData("keyboard", EndReason.KeyboardMissing)]
    [InlineData("driver missing", EndReason.DriverMissing)]
    [InlineData("driver not started", EndReason.DriverNotStarted)]
    [InlineData("game", EndReason.GameNotRunning)]
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
            case "game":
                _gameRunning = false;
                break;
        }

        Assert.Equal(reason, _session.WhyNotStartable(KeyboardId, GamePath)?.Reason);
        var end = await _session.StartAsync(Request());

        Assert.Equal(reason, end?.Reason);
        Assert.Equal(end, _session.LastEnd);
        Assert.Equal(SessionState.Idle, _session.State);
        Assert.Empty(_driver.Log);
        Assert.Null(_foreground);
    }

    [Fact]
    public async Task Start_connects_the_pad_at_neutral_installs_the_hook_and_gates_the_analog_keys()
    {
        // A sensor that never answers: no reading clears the start gate.
        _openSensor = () => new FakeVendorStream { OnRead = (_, _, _) => { Thread.Sleep(5); return null; } };
        Assert.Null(_session.WhyNotStartable(KeyboardId, GamePath));

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
        await StartRunning();
        _foreground!.Gain();
        Assert.True(Press(Space, down: true));
        WaitForPad(ButtonA);

        _foreground.Lose();

        WaitForPad(PadReport.Neutral);
        Assert.False(Press(Space, down: false), "the key-up of a key held through the loss reaches the desktop");
        Assert.Equal(SessionState.Running, _session.State);
        Assert.False(_session.Status().GameHasFocus);
    }

    [Fact]
    public async Task Regaining_focus_resumes_mapping_but_a_key_held_through_it_stays_dead_until_released()
    {
        await StartRunning();
        _foreground!.Gain();
        Press(Space, down: true);
        WaitForPad(ButtonA);
        _foreground.Lose();
        WaitForPad(PadReport.Neutral);

        _foreground.Gain();

        StaysAt(PadReport.Neutral);
        Assert.True(_session.Status().KeysAwaitingRelease);
        Press(Space, down: false);
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
        await StartRunning();
        _foreground!.Gain();
        Press(Space, down: true);
        WaitForPad(ButtonA);

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

    [Theory]
    [InlineData(EndReason.Hotkey)]
    [InlineData(EndReason.GameExited)]
    [InlineData(EndReason.ControllerDisconnected)]
    [InlineData(EndReason.SleepOrWake)]
    [InlineData(EndReason.UserStop)]
    [InlineData(EndReason.ProfileEdited)]
    [InlineData(EndReason.AppClosing)]
    public async Task Every_stop_zeroes_then_unplugs_the_pad_and_removes_the_hook(EndReason reason)
    {
        await StartRunning();
        _foreground!.Gain();
        Press(Space, down: true);
        WaitForPad(ButtonA);
        var hook = _session.Hook!;

        switch (reason)
        {
            case EndReason.Hotkey:
                hook.StopRequested!();
                break;
            case EndReason.GameExited:
                _game.Exit();
                break;
            case EndReason.ControllerDisconnected:
                _driver.Gone = true;
                break;
            case EndReason.SleepOrWake:
                _power.Raise();
                break;
            case EndReason.AppClosing:
                _session.Dispose();
                break;
            default:
                await _session.StopAsync(reason);
                break;
        }

        AssertStoppedCleanly(reason);
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

    [Theory]
    [InlineData("backoff")]
    [InlineData("blocked read")]
    public async Task The_hotkey_stops_at_once_while_the_sensor_waits(string wait)
    {
        FakeVendorStream? blocked = null;
        _openSensor = wait == "backoff"
            ? () => null
            : () =>
            {
                blocked = new FakeVendorStream();
                blocked.OnRead = (_, _, _) =>
                {
                    blocked.BlockUntilDisposed();
                    return null;
                };
                return blocked;
            };
        await StartRunning();
        Thread.Sleep(100);
        var clock = Stopwatch.StartNew();

        _session.Hook!.StopRequested!();

        Eventually(() => _session.State == SessionState.Idle, "stop");
        Assert.True(clock.ElapsedMilliseconds < 500, $"Stop took {clock.ElapsedMilliseconds} ms with the sensor in a {wait}.");
        Assert.Equal(EndReason.Hotkey, _session.LastEnd!.Reason);
        Assert.DoesNotContain("did not stop", _session.LastEnd.Message);
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

    [Fact]
    public async Task A_stalled_engine_is_unplugged_by_the_watchdog_and_its_late_return_never_reaches_the_driver()
    {
        using var release = new ManualResetEventSlim(false);
        var wedge = 0;
        _driver.OnSubmit = _ =>
        {
            if (Volatile.Read(ref wedge) == 1 && Thread.CurrentThread.Name == "apex-engine")
            {
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        await StartRunning();
        _foreground!.Gain();
        Volatile.Write(ref wedge, 1);
        var clock = Stopwatch.StartNew();

        Press(Space, down: true);

        Eventually(() => !_driver.Connected, "the watchdog to unplug the pad", 2000);
        var unpluggedAfter = clock.ElapsedMilliseconds;
        Eventually(() => _session.State == SessionState.Idle, "the session to stop");
        var log = _driver.Log;
        release.Set();
        Thread.Sleep(50);

        Assert.InRange(unpluggedAfter, Watchdog.StallMs, 1000);
        Assert.Equal(EndReason.EngineStalled, _session.LastEnd!.Reason);
        Assert.Contains("engine thread did not stop", _session.LastEnd.Message);
        Assert.Equal(["submit neutral", "disconnect"], log.Skip(log.Count - 2));
        Assert.Equal([.. log, FakePadDriver.Describe(ButtonA)], _driver.Log);
        Assert.Equal(_latencyBefore, GCSettings.LatencyMode);
    }

    [Fact]
    public async Task A_hook_windows_removed_is_noticed_and_put_back_with_keys_gated()
    {
        await StartRunning();
        _foreground!.Gain();
        var first = _session.Hook;

        // Raw Input keeps counting keys; the hook's count stands still, as when Windows drops it.
        var clock = Stopwatch.StartNew();
        while (_session.Status().HookReinstalls == 0 && clock.ElapsedMilliseconds < 3000)
        {
            Interlocked.Increment(ref _rawEvents);
            Thread.Sleep(10);
        }

        Assert.Equal(1, _session.Status().HookReinstalls);
        Assert.NotSame(first, _session.Hook);
        Assert.True(_session.Hook!.IsInstalled);
        Assert.False(first!.IsInstalled);
        Assert.Equal(SessionState.Running, _session.State);
        Assert.True(Press(Space, down: true));
        WaitForPad(ButtonA);
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
    public async Task A_pad_that_cannot_connect_fails_the_start_with_the_driver_s_reason_and_unwinds()
    {
        FakeVendorStream? stream = null;
        _openSensor = () => stream = SlowStream();
        _driver.ConnectThrows = new VigemNoFreeSlotException();

        var end = await _session.StartAsync(Request());

        Assert.Equal(EndReason.PadFailed, end?.Reason);
        Assert.Contains("controller slot", end!.Message);
        Assert.Equal(SessionState.Idle, _session.State);
        Eventually(() => stream?.IsDisposed == true, "the sensor to stop");
        Assert.True(_game.Disposed);
    }

    [Fact]
    public async Task A_second_start_while_running_is_refused()
    {
        await StartRunning();

        var end = await _session.StartAsync(Request());

        Assert.Equal(EndReason.StartFailed, end?.Reason);
        Assert.Equal(SessionState.Running, _session.State);
    }
}
