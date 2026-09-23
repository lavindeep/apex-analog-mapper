using ApexMapper.App.Model;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
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
        Assert.Equal("Choose a keyboard that is plugged in.", status.Blocker);
        Assert.False(status.Start.CanExecute(null));

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

        _h.Session.NotStartable = SessionEnd.For(EndReason.DriverMissing);
        status.Tick(_h.Now);
        Assert.Equal(SessionEnd.For(EndReason.DriverMissing).Message, status.Blocker);
        Assert.False(status.CanStart);
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
        Assert.Equal("Stopped.", status.Reason);
    }

    [Fact]
    public async Task A_key_on_its_on_and_off_state_shows_why()
    {
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();

        _h.Session.NextStatus = new SessionStatus(
            SessionState.Running, null, GameRunning: true, GameHasFocus: true, GameElevated: false, FallbackKeys: 2,
            SensorProblem: "The keyboard stopped answering.", KeysAwaitingRelease: false, SubmitCount: 0, HookReinstalls: 0, RestartRequired: false);
        status.Tick(_h.Now);

        Assert.Contains("2 analog keys follow the keyboard's on and off state instead of its depth. The keyboard stopped answering.", status.Warnings);
    }

    [Fact]
    public async Task A_key_that_stays_at_the_sensor_s_limit_warns_after_a_few_seconds()
    {
        const string Warning = "W reads the sensor's maximum, past the full press it was calibrated with, so it reaches full output early. Calibrate it again.";
        _h.MakeReady();
        var status = Create();
        await status.StartAsync();
        var atLimit = new SessionStatus(SessionState.Running, null, true, true, false, 0, null, false, 0, 0, false, KeysAtLimit: [DefaultProfiles.Key.W]);

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
        var running = new SessionStatus(SessionState.Running, null, true, true, false, 0, null, false, SubmitCount: 500, 0, false, CycleP50Ms: 1.2f, CycleP99Ms: 2.5f);

        _h.Session.NextStatus = running;
        status.Tick(_h.Now);
        _h.Session.NextStatus = running with { SubmitCount = 1500 };
        status.Tick(_h.Now + 1000);

        Assert.EndsWith("Controller updates: 1000 a second.", status.Timing);
    }

    [Fact]
    public void Restart_is_offered_only_when_a_controller_could_not_be_removed()
    {
        _h.MakeReady();
        var status = Create();
        Assert.False(status.RestartRequired);

        _h.Session.RestartRequired = true;
        status.Tick(_h.Now);

        Assert.True(status.RestartRequired);
        Assert.Equal(SessionEnd.For(EndReason.RestartRequired).Message, status.Blocker);
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
