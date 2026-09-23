using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class KeyboardViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private KeyboardViewModel Create(Guid? remembered = null, IEnumerable<Guid>? consented = null, Workspace? workspace = null)
    {
        var keyboard = new KeyboardViewModel(_h.Services, workspace ?? _h.Workspace, remembered, consented);
        keyboard.OnKeyboards(_h.Boards);
        return keyboard;
    }

    /// <summary>Starts the sensor check and lets it take its reading at rest.</summary>
    private long CheckAtRest(KeyboardViewModel keyboard)
    {
        keyboard.Check.Execute(null);
        _h.Sensor.Reading = FakeSensor.AtRest();
        var rest = _h.Now + CalibrationViewModel.SettleMs;
        keyboard.Tick(rest);
        return rest;
    }

    [Fact]
    public void A_lone_board_is_chosen_remembered_and_asked_its_firmware()
    {
        var keyboard = Create();

        Assert.Equal(AppHarness.Tkl, keyboard.Selected?.Id);
        Assert.Equal(AppHarness.Tkl, _h.SavedSettings.Keyboard);
        Assert.Equal([AppHarness.Tkl], _h.FirmwareRequests);
        Assert.Equal("Firmware 4.16.8", keyboard.FirmwareText);
        Assert.Null(keyboard.FirmwareWarning);
        Assert.False(keyboard.IsUnverified);
        Assert.False(keyboard.NeedsConsent);
    }

    [Fact]
    public void With_nothing_plugged_in_the_card_asks_for_the_keyboard()
    {
        _h.Boards.Clear();

        var keyboard = Create();

        Assert.Equal("Not plugged in", keyboard.Summary);
        Assert.Equal("Plug in your Apex Pro. It shows up here as soon as Windows finds it.", keyboard.Missing);
        Assert.Empty(_h.FirmwareRequests);
    }

    [Fact]
    public void A_choice_among_several_boards_is_remembered()
    {
        _h.Boards.Add(AppHarness.Gen3Info);
        var keyboard = Create();
        Assert.Null(keyboard.Selected);
        Assert.Equal("No keyboard chosen", keyboard.Summary);
        Assert.Equal("Choose your keyboard.", keyboard.Missing);
        Assert.Null(_h.Workspace.Board);

        keyboard.Selected = keyboard.Boards.Single(b => b.Id == AppHarness.Gen3);

        Assert.Equal(AppHarness.Gen3, _h.SavedSettings.Keyboard);
        Assert.Equal(AppHarness.Gen3, _h.Workspace.Board?.Id);
        Assert.Equal("Apex Pro TKL Gen 3", keyboard.Summary);
        Assert.Null(keyboard.Missing);
    }

    [Fact]
    public void After_a_startup_that_could_not_read_the_settings_only_the_user_s_choices_are_saved()
    {
        var other = Guid.NewGuid();
        _h.Services.Settings.Save(new AppSettings(Keyboard: other, ConsentedKeyboards: [other]));
        var workspace = new Workspace { SettingsUnread = true, SettingsProblem = "Settings could not be read." };
        _h.Boards[0] = AppHarness.Gen3Info;

        var keyboard = Create(workspace: workspace);

        Assert.Equal(AppHarness.Gen3, keyboard.Selected?.Id);
        Assert.Equal(other, _h.SavedSettings.Keyboard);

        keyboard.Consent.Execute(null);

        Assert.Equal([other, AppHarness.Gen3], _h.SavedSettings.ConsentedKeyboards);
        Assert.Equal("Settings could not be read.", workspace.SettingsProblem);
    }

    [Fact]
    public void A_replug_during_a_session_waits_for_it_to_end()
    {
        var keyboard = Create();
        var board = _h.Workspace.Board;
        _h.Workspace.Session = SessionState.Running;

        // The session's poller has the board open, so nothing is sent to it.
        keyboard.OnKeyboards([]);
        Assert.Same(board, _h.Workspace.Board);
        keyboard.OnKeyboards([AppHarness.TklInfo]);
        keyboard.OnKeyboards([]);
        Assert.Single(_h.FirmwareRequests);

        _h.Workspace.Session = SessionState.Idle;
        Assert.Null(_h.Workspace.Board);
    }

    [Fact]
    public void The_board_cannot_change_while_a_session_runs()
    {
        _h.Boards.Add(AppHarness.Gen3Info);
        var keyboard = Create(remembered: AppHarness.Tkl);
        _h.Workspace.Session = SessionState.Running;

        keyboard.Selected = keyboard.Boards.Single(b => b.Id == AppHarness.Gen3);

        Assert.Equal(AppHarness.Tkl, keyboard.Selected?.Id);
        Assert.False(keyboard.CanChoose);
    }

    [Fact]
    public void Untested_firmware_warns_without_refusing()
    {
        _h.FirmwareOf = _ => new FirmwareReading("4.20.0", null, null);

        var keyboard = Create();

        Assert.Equal("Firmware 4.20.0", keyboard.FirmwareText);
        Assert.StartsWith("The app has not been tested with firmware 4.20.0.", keyboard.FirmwareWarning);
        Assert.True(_h.Workspace.Board!.CanReadSensors);
    }

    [Fact]
    public void An_untested_board_is_labelled_and_its_sensors_are_not_read_until_the_user_agrees()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create();

        Assert.True(keyboard.IsUnverified);
        // The untested banner says it; the firmware notice would say it again.
        Assert.Null(keyboard.FirmwareWarning);
        Assert.True(keyboard.NeedsConsent);
        Assert.False(keyboard.CanCheck);
        Assert.False(_h.Workspace.Board!.CanReadSensors);
        Assert.False(_h.Sensor.Wanted);

        keyboard.Consent.Execute(null);

        Assert.True(_h.Workspace.Board!.CanReadSensors);
        Assert.Equal([AppHarness.Gen3], _h.SavedSettings.ConsentedKeyboards);
        Assert.True(keyboard.IsChecking);
        Assert.True(_h.Sensor.Wanted);
        Assert.True(keyboard.IsUnverified);
    }

    [Fact]
    public void An_untested_board_whose_firmware_reply_is_not_a_version_is_never_asked_for_its_sensors()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        _h.FirmwareOf = _ => new FirmwareReading(null, [0x00, 0xFF], "The reply did not hold a firmware version.");

        var keyboard = Create();

        Assert.False(keyboard.NeedsConsent);
        Assert.False(keyboard.CanCheck);
        Assert.False(_h.Workspace.Board!.CanReadSensors);
        // The reply is still worth sending in.
        Assert.True(keyboard.Export.CanExecute(null));
    }

    [Fact]
    public void Consent_given_in_an_earlier_run_holds()
    {
        _h.Boards[0] = AppHarness.Gen3Info;

        var keyboard = Create(consented: [AppHarness.Gen3]);

        Assert.False(keyboard.NeedsConsent);
        Assert.True(keyboard.CanCheck);
        Assert.False(_h.Sensor.Wanted);
    }

    [Fact]
    public void The_check_passes_when_a_sensor_moves_and_the_export_carries_what_it_read()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        byte[] firmwareReply = [0x00, 0x31, 0x2E, 0x30];
        _h.FirmwareOf = _ => new FirmwareReading("1.0", firmwareReply, null);
        var keyboard = Create(consented: [AppHarness.Gen3]);

        // Enter clicked the button and is still on its way up: not a reading at rest.
        keyboard.Check.Execute(null);
        _h.Sensor.Reading = AppHarness.Holding(50, 3000);
        keyboard.Tick(_h.Now);
        var rest = CheckAtRest(keyboard);
        Assert.True(keyboard.IsChecking);
        _h.Sensor.Reading = AppHarness.Holding(16, 3000);
        keyboard.Tick(rest + 100);

        Assert.False(keyboard.IsChecking);
        Assert.StartsWith("The sensors respond to key presses.", keyboard.CheckText);
        Assert.False(_h.Sensor.Wanted);

        _h.Dialogs.SavePath = _h.File("capture.json");
        // Unplugged while the dialog is open: the capture is still of the board it was asked for.
        _h.Dialogs.WhileAsking = () =>
        {
            _h.Boards.Clear();
            _h.Keyboards.Refresh();
        };
        keyboard.Export.Execute(null);

        Assert.Equal("Saved. Attach the file to a new issue on GitHub.", keyboard.CheckText);
        Assert.True(keyboard.Exported);
        keyboard.OpenIssue.Execute(null);
        Assert.Equal([KeyboardViewModel.NewIssuePage], _h.Opened);
        var export = CaptureExport.FromJson(File.ReadAllText(_h.Dialogs.SavePath), out var error);

        Assert.Null(error);
        Assert.Equal(0x1642, export!.ProductId);
        Assert.Equal((65, 65), (export.InputReportLength, export.OutputReportLength));
        Assert.Equal("1.0", export.Firmware);
        Assert.Equal("00312E30", export.Replies.Single(r => r.Label == "firmware").ReplyHex);
        Assert.Equal(5, export.Replies.Count(r => r.Label == "at rest"));
        var held = export.Replies.Single(r => r.Label == "key held" && r.Group == 2);
        var raw = new ushort[SensorProtocol.SensorsPerGroup];
        Assert.Null(SensorProtocol.ParseGroup(Convert.FromHexString(held.ReplyHex), raw, new ushort[SensorProtocol.SensorsPerGroup]));
        Assert.Equal(3000, raw[2]);
        // The reading at rest was taken after Enter came up.
        var restGroup = export.Replies.Single(r => r.Label == "at rest" && r.Group == 50 / SensorProtocol.SensorsPerGroup + 1);
        Assert.Null(SensorProtocol.ParseGroup(Convert.FromHexString(restGroup.ReplyHex), raw, new ushort[SensorProtocol.SensorsPerGroup]));
        Assert.Equal(850, raw[50 % SensorProtocol.SensorsPerGroup]);
    }

    [Fact]
    public void The_check_asks_for_a_capture_when_nothing_moves()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        var rest = CheckAtRest(keyboard);

        keyboard.Tick(rest + KeyboardViewModel.CheckTimeoutMs);
        Assert.True(keyboard.IsChecking);
        keyboard.Tick(rest + KeyboardViewModel.CheckTimeoutMs + 1);

        Assert.False(keyboard.IsChecking);
        Assert.StartsWith("No sensor moved", keyboard.CheckText);
    }

    [Fact]
    public void A_check_with_no_readings_gives_up_and_one_a_session_interrupts_says_so()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);

        keyboard.Check.Execute(null);
        keyboard.Tick(_h.Now + KeyboardViewModel.CheckTimeoutMs + 1);
        Assert.False(keyboard.IsChecking);
        Assert.False(_h.Sensor.Wanted);

        keyboard.Check.Execute(null);
        _h.Workspace.Session = SessionState.Starting;
        Assert.False(keyboard.IsChecking);
        Assert.False(_h.Sensor.Wanted);
        Assert.Equal("The check stopped when mapping started. Run it again once mapping stops.", keyboard.CheckText);
    }

    [Fact]
    public void A_firmware_request_that_throws_leaves_the_reason_on_the_card()
    {
        _h.FirmwareOf = _ => throw new IOException("The device is busy.");

        var keyboard = Create();

        Assert.Equal("The firmware could not be read. The device is busy.", keyboard.FirmwareText);
        Assert.False(_h.Workspace.Board!.CanReadSensors);
    }
}
