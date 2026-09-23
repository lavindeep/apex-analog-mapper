using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class StatusViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private StatusViewModel Create() => new(_h.Services, _h.Workspace);

    private static bool MentionsOtherKeyboard(string warning) => warning.Contains("another keyboard", StringComparison.Ordinal);

    [Fact]
    public void Start_says_what_is_missing_until_everything_is_in_place()
    {
        var status = Create();
        Assert.Equal("Plug in your Apex Pro keyboard.", status.Blocker);
        Assert.False(status.Start.CanExecute(null));

        _h.Keyboards.Refresh();
        status.Tick(_h.Now);
        Assert.Equal("Choose your keyboard on the keyboard card.", status.Blocker);
        _h.Workspace.ReadingFirmware = true;
        Assert.Equal("Reading the keyboard's firmware.", status.Blocker);
        _h.Workspace.ReadingFirmware = false;

        _h.Workspace.Board = new Board(AppHarness.Gen3Info, new FirmwareReading("1.0.0", null, null), Consented: false);
        status.Tick(_h.Now);
        Assert.Equal("This keyboard has not been tested. Try it on the keyboard card first.", status.Blocker);

        _h.ChooseTkl();
        status.Tick(_h.Now);
        Assert.Equal("Choose the game.", status.Blocker);

        _h.Workspace.GamePath = AppHarness.Game;
        _h.Workspace.ActiveProfile = DefaultProfiles.Forza();
        status.Tick(_h.Now);
        Assert.StartsWith("Calibrate ", status.Blocker);
        Assert.Contains("W", status.Blocker);
        Assert.True(status.BlockedByCalibration);

        _h.CalibrateForza();
        _h.ChooseTkl();
        status.Tick(_h.Now);
        Assert.Null(status.Blocker);
        Assert.False(status.BlockedByCalibration);
        Assert.True(status.Start.CanExecute(null));
        Assert.Equal("Ready. Press Start, then launch the game.", status.Reason);

        _h.Session.NotStartable = SessionEnd.For(EndReason.DriverMissing);
        status.Tick(_h.Now);
        Assert.Equal(SessionEnd.For(EndReason.DriverMissing).Message, status.Blocker);
        Assert.False(status.CanStart);
        Assert.Null(status.Reason);
    }

    [Fact]
    public void A_missing_driver_is_the_first_thing_start_waits_for_and_the_card_links_its_page()
    {
        var status = Create();

        _h.Workspace.Driver = DriverState.Missing;
        Assert.Equal("Install the ViGEmBus controller driver first.", status.Blocker);
        Assert.True(status.BlockedByDriver);
        status.OpenDriverPage.Execute(null);
        Assert.Equal([SetupViewModel.DriverPage], _h.Opened);

        _h.Workspace.Driver = DriverState.NotStarted;
        Assert.Equal(SessionEnd.For(EndReason.DriverNotStarted).Message, status.Blocker);
        Assert.False(status.BlockedByDriver);

        _h.Workspace.Driver = DriverState.Running;
        Assert.Equal("Plug in your Apex Pro keyboard.", status.Blocker);
    }

    [Fact]
    public async Task The_workspace_says_starting_before_the_session_opens_the_board_and_a_start_that_throws_leaves_it_stopped()
    {
        _h.MakeReady();
        var status = Create();
        SessionState? seen = null;
        _h.Session.Starting = () => seen = _h.Workspace.Session;
        _h.Session.StartFault = new InvalidOperationException("The pad could not be connected.");

        await status.StartAsync();

        // The live sensor lets go of the board on Starting.
        Assert.Equal(SessionState.Starting, seen);
        Assert.Equal(SessionState.Idle, _h.Workspace.Session);
        Assert.True(status.Start.CanExecute(null));
        Assert.Contains(_h.Log, line => line.StartsWith("Start failed: System.InvalidOperationException: The pad could not be connected.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_log_has_each_session_and_never_a_key()
    {
        _h.MakeReady();
        var status = Create();
        var profile = new ProfileViewModel(_h.Services, _h.Workspace, null);
        profile.AddKey.Execute(null);
        profile.OnKey(new RawKeyEvent(new ScanCode(0x25), true, 1, ProfileViewModel.CaptureArmTicks));

        await status.StartAsync();
        status.OnKey(new RawKeyEvent(new ScanCode(0x25), true, 2, 0));
        await status.StopAsync();

        Assert.Contains("Session Running.", _h.Log);
        Assert.Contains($"Session ended (UserStop): {SessionEnd.For(EndReason.UserStop).Message}", _h.Log);
        Assert.DoesNotContain(_h.Log, line => line.Contains("0x25", StringComparison.Ordinal));
    }

    [Fact]
    public void An_untested_board_the_user_agreed_to_try_can_start_and_is_named_on_the_card()
    {
        var store = _h.Services.Calibrations;
        foreach (var (key, index) in new[] { (DefaultProfiles.Key.W, 16), (DefaultProfiles.Key.S, 30), (DefaultProfiles.Key.A, 29), (DefaultProfiles.Key.D, 31) })
        {
            store.Put(AppHarness.Gen3, "1.0", key, KeyCalibration.Create(850, 3900, 40, index));
        }
        _h.Workspace.Board = new Board(AppHarness.Gen3Info, new FirmwareReading("1.0", null, null), Consented: true);
        _h.Workspace.Calibration = store.Load(AppHarness.Gen3, "1.0");
        _h.Workspace.GamePath = AppHarness.Game;
        _h.Workspace.ActiveProfile = DefaultProfiles.Forza();

        var status = Create();

        Assert.Null(status.Blocker);
        Assert.Contains("The Apex Pro TKL Gen 3 has not been tested with this app.", status.Warnings);
    }

    [Fact]
    public void A_setting_that_cannot_be_saved_says_so_on_the_status_card()
    {
        _h.Services.Settings.Save(new AppSettings());
        var status = Create();

        using (new FileStream(_h.File("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _h.Workspace.Remember(_h.Services.Settings, s => s with { GamePath = AppHarness.Game });
        }

        Assert.StartsWith("Settings could not be saved", _h.Workspace.SettingsProblem);
        Assert.Contains(_h.Workspace.SettingsProblem, status.Warnings);
    }

    [Fact]
    public void The_calibration_link_asks_for_the_calibration_card()
    {
        var status = Create();
        var asked = 0;
        status.CalibrationRequested += () => asked++;

        status.GoToCalibration.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Start_hands_the_session_the_compiled_profile_and_the_stored_signatures()
    {
        _h.MakeReady();
        var status = Create();

        await status.StartAsync();

        var request = Assert.Single(_h.Session.Starts);
        Assert.Equal(AppHarness.Tkl, request.Keyboard);
        Assert.Equal(AppHarness.Game, request.GamePath);
        Assert.Equal(new GroupSignature(0x2000), request.Signatures![2]);
        Assert.Equal(new GroupSignature(0x1000), request.Signatures[3]);
        Assert.Equal(SessionState.Running, _h.Workspace.Session);
        Assert.Equal(ProfileJson.Serialize(DefaultProfiles.Forza()), _h.Workspace.RunningProfileText);
        Assert.Equal("Mapping", status.StateText);
        Assert.False(status.Start.CanExecute(null));
        Assert.True(status.Stop.CanExecute(null));

        await status.StopAsync();

        Assert.Equal([EndReason.UserStop], _h.Session.Stops);
        Assert.Equal(SessionState.Idle, _h.Workspace.Session);
        Assert.Null(_h.Workspace.RunningProfileText);
        Assert.Equal("Stopped", status.StateText);
        Assert.Equal("Ready. Press Start, then launch the game.", status.Reason);
    }

    [Fact]
    public async Task A_key_on_its_on_and_off_state_shows_why()
    {
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();

        _h.Session.NextStatus = new SessionStatus(
            SessionState.Running, null, GameRunning: true, GameHasFocus: true, GameElevation: Elevation.Visible, FallbackKeys: 2,
            SensorProblem: "The keyboard stopped answering.", KeysAwaitingRelease: false, SubmitCount: 0, HookReinstalls: 0, RestartRequired: false);
        status.Tick(_h.Now);

        const string Warning = "2 analog keys are on or off only, because the keyboard stopped sending how far keys are pressed. They go back to analog when readings return. " +
            "If this lasts, stop mapping, then unplug the keyboard and plug it back in.";
        Assert.Contains(Warning, status.Warnings);
        Assert.Contains("Warning: " + Warning, _h.Log);
        Assert.Contains("Sensor fault: The keyboard stopped answering.", _h.Log);

        // Logged once each, not every time the card reads the status.
        status.Tick(_h.Now + StatusViewModel.RefreshMs);
        Assert.Single(_h.Log, line => line.StartsWith("Sensor fault", StringComparison.Ordinal));
        Assert.Single(_h.Log, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_the_game_is_not_running_the_state_says_mapping_waits_for_it()
    {
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();

        _h.Session.NextStatus = new SessionStatus(SessionState.Running, null, GameRunning: false, false, Elevation.Visible, 0, null, false, 0, 0, false);
        status.Tick(_h.Now);
        Assert.Equal("Mapping, waiting for the game", status.StateText);
        Assert.Empty(status.Warnings);

        _h.Session.NextStatus = _h.Session.NextStatus with { GameRunning = true, KeysAwaitingRelease = true, HookReinstalls = 2 };
        status.Tick(_h.Now + StatusViewModel.RefreshMs);
        Assert.Equal("Mapping", status.StateText);
        Assert.Contains("A key held while the game came back to the front stays off until you release it once.", status.Warnings);
        Assert.Contains("Windows turned off key blocking 2 times, and the app turned it back on each time.", status.Warnings);
    }

    [Fact]
    public async Task A_game_the_hook_cannot_see_says_why_and_one_windows_would_not_describe_is_not_called_elevated()
    {
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();
        var elevated = new SessionStatus(SessionState.Running, null, true, true, Elevation.Elevated, 0, null, false, 0, 0, false);

        _h.Session.NextStatus = elevated;
        status.Tick(_h.Now);
        Assert.Contains("This app cannot see the keys of the game, which runs as administrator. Close this app, then right-click it and choose Run as administrator.", status.Warnings);

        _h.Session.NextStatus = elevated with { GameElevation = Elevation.Unknown };
        status.Tick(_h.Now + StatusViewModel.RefreshMs);
        Assert.Contains("Windows would not say whether the game runs as administrator. If its keys do not reach it, close this app, then right-click it and choose Run as administrator.", status.Warnings);
        Assert.DoesNotContain(status.Warnings, w => w.StartsWith("This app cannot see", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_that_stays_at_the_sensor_s_limit_warns_after_a_few_seconds()
    {
        const string Warning = "W presses deeper than when it was calibrated, so it reaches full output early. Stop mapping and calibrate W again.";
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();
        var atLimit = new SessionStatus(SessionState.Running, null, true, true, Elevation.Visible, 0, null, false, 0, 0, false, KeysAtLimit: [DefaultProfiles.Key.W]);

        _h.Session.NextStatus = atLimit;
        status.Tick(_h.Now);
        status.Tick(_h.Now + StatusViewModel.AtLimitWarningMs - 1);
        Assert.DoesNotContain(Warning, status.Warnings);

        status.Tick(_h.Now + StatusViewModel.AtLimitWarningMs);
        Assert.Contains(Warning, status.Warnings);

        _h.Session.NextStatus = atLimit with { KeysAtLimit = [] };
        status.Tick(_h.Now + StatusViewModel.AtLimitWarningMs + 250);
        Assert.DoesNotContain(Warning, status.Warnings);
    }

    [Fact]
    public async Task The_controller_update_rate_is_counted_over_a_second()
    {
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();
        var running = new SessionStatus(SessionState.Running, null, true, true, Elevation.Visible, 0, null, false, SubmitCount: 500, 0, false, CycleP50Ms: 1.2f, CycleP99Ms: 2.5f);

        _h.Session.NextStatus = running;
        status.Tick(_h.Now);
        _h.Session.NextStatus = running with { SubmitCount = 1500 };
        status.Tick(_h.Now + 1000);

        Assert.Equal($"Keyboard read every {1.2:0.0} ms ({2.5:0.0} ms for the slowest 1%). Controller updated 1000 times a second.", status.Timing);
    }

    [Fact]
    public void Restart_is_offered_only_when_a_controller_could_not_be_removed()
    {
        _h.MakeReady();
        var status = Create();
        Assert.False(status.RestartRequired);

        _h.Session.RestartRequired = true;
        _h.Session.LastEnd = SessionEnd.For(EndReason.EngineStalled);
        status.Tick(_h.Now);

        Assert.True(status.RestartRequired);
        Assert.Equal(SessionEnd.For(EndReason.RestartRequired).Message, status.Blocker);
        // The stall's own message ends with "Press Start to try again", which a restart has to come before.
        Assert.Null(status.Reason);
        Assert.Empty(status.Warnings);
        status.Restart.Execute(null);
        Assert.Equal(1, _h.Restarts);
    }

    [Fact]
    public async Task A_mapped_key_from_another_keyboard_shows_the_notice_once_per_run_of_the_app()
    {
        _h.MakeReady();
        _h.Keys.Containers[1] = AppHarness.Tkl;
        _h.Keys.Containers[2] = Guid.NewGuid();
        var status = Create();
        await status.StartAsync();

        status.OnKey(new RawKeyEvent(DefaultProfiles.Key.W, true, 1, 0));
        status.OnKey(new RawKeyEvent(new ScanCode(0x25), true, 2, 0));
        Assert.DoesNotContain(status.Warnings, MentionsOtherKeyboard);

        status.OnKey(new RawKeyEvent(DefaultProfiles.Key.W, true, 2, 0));
        Assert.Contains(status.Warnings, MentionsOtherKeyboard);

        await status.StopAsync();
        Assert.DoesNotContain(status.Warnings, MentionsOtherKeyboard);

        await status.StartAsync();
        status.OnKey(new RawKeyEvent(DefaultProfiles.Key.W, true, 2, 0));
        Assert.DoesNotContain(status.Warnings, MentionsOtherKeyboard);
    }
}
