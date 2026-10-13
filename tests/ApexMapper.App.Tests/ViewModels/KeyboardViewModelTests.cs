using System.IO;
using System.Text.Json;
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
        var raised = new List<string?>();
        keyboard.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        keyboard.Selected = keyboard.Boards.Single(b => b.Id == AppHarness.Gen3);

        Assert.Equal(AppHarness.Gen3, _h.SavedSettings.Keyboard);
        Assert.Equal(AppHarness.Gen3, _h.Workspace.Board?.Id);
        Assert.Equal("Apex Pro TKL Gen 3", keyboard.Summary);
        Assert.Null(keyboard.Missing);
        Assert.Contains(nameof(keyboard.Missing), raised);
    }

    [Fact]
    public void After_a_startup_that_could_not_read_the_settings_only_the_user_s_choices_are_saved()
    {
        var other = Guid.NewGuid();
        _h.Services.Settings.Save(new AppSettings(Keyboard: other, ConsentedKeyboards: [other], ActiveProfile: "profile-7"));
        var workspace = new Workspace { SettingsUnread = true, SettingsProblem = "Settings could not be read." };
        _h.Boards[0] = AppHarness.Gen3Info;

        var keyboard = Create(workspace: workspace);
        _ = new ProfileViewModel(_h.Services, workspace, remembered: null);

        Assert.Equal(AppHarness.Gen3, keyboard.Selected?.Id);
        Assert.Equal((other, "profile-7"), (_h.SavedSettings.Keyboard, _h.SavedSettings.ActiveProfile));

        keyboard.Consent.Execute(null);

        Assert.Equal([other, AppHarness.Gen3], _h.SavedSettings.ConsentedKeyboards);
        Assert.Equal("Settings could not be read.", workspace.SettingsProblem);

        // A failed save shows until a save works, and then the startup problem is back.
        var raised = 0;
        workspace.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(Workspace.SettingsProblem) ? 1 : 0;
        using (new FileStream(_h.File("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            workspace.Remember(_h.Services.Settings, settings => settings);
        }
        Assert.StartsWith("Settings could not be saved", workspace.SettingsProblem);
        workspace.Remember(_h.Services.Settings, settings => settings);
        Assert.Equal(("Settings could not be read.", 2), (workspace.SettingsProblem, raised));
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
        _h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        var keyboard = Create();

        Assert.True(keyboard.IsUnverified);
        // The untested banner says it; the notice for firmware no one has tried would say it again.
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
    public void An_export_carries_its_own_board_s_report_lengths_and_none_of_another_board_s_readings()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        var rest = CheckAtRest(keyboard);
        _h.Sensor.Reading = AppHarness.Holding(16, 3000);
        keyboard.Tick(rest + 100);
        Assert.False(keyboard.IsChecking);

        // Swapped for a board whose vendor interface reports other lengths.
        _h.Boards[0] = AppHarness.TklInfo with { HasVendorInterface = false, VendorInputLength = 64, VendorOutputLength = 32 };
        keyboard.OnKeyboards(_h.Boards);
        keyboard.Selected = keyboard.Boards.Single();
        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);

        var export = CaptureExport.FromJson(File.ReadAllText(_h.Dialogs.SavePath), out var error);
        Assert.Null(error);
        Assert.Equal((0x1614, 64, 32), (export!.ProductId, export.InputReportLength, export.OutputReportLength));
        Assert.DoesNotContain(export.Replies, reply => reply.Label is "at rest" or "key held");
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
    public void A_check_whose_replies_fail_exports_the_reply_the_keyboard_sent()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        var rejected = new CapturedReply("rejected: Reply padding was not zero.", 1, "00AB");

        keyboard.Check.Execute(null);
        _h.Sensor.Problem = "Reply padding was not zero.";
        _h.Sensor.Rejected = rejected;
        keyboard.Tick(_h.Now + KeyboardViewModel.CheckTimeoutMs + 1);
        // The check stopped the poller, and the reply went with it.
        _h.Sensor.Rejected = null;

        Assert.Equal("The keyboard's sensors could not be read: Reply padding was not zero.", keyboard.CheckText);
        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);
        var export = CaptureExport.FromJson(File.ReadAllText(_h.Dialogs.SavePath), out var error);
        Assert.Null(error);
        Assert.Equal(rejected, export!.Replies.Single(r => r.Label.StartsWith("rejected", StringComparison.Ordinal)));
    }

    /// <summary>A replug cancels the check before it times out. The reply rejected during it still goes in the capture.</summary>
    [Fact]
    public void A_check_cut_short_by_a_replug_still_exports_its_rejected_reply()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        keyboard.Check.Execute(null);
        var rejected = new CapturedReply("rejected: Reply padding was not zero.", 1, "00AB");
        _h.Sensor.Rejected = rejected;

        keyboard.OnKeyboards([]);
        keyboard.OnKeyboards([AppHarness.Gen3Info]);
        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);

        Assert.Equal([rejected], RejectedIn(_h.Dialogs.SavePath));
    }

    /// <summary>The poller can reject a reply while it stops, after the check has let go of the sensor.</summary>
    [Fact]
    public void A_reply_rejected_while_the_poller_stops_still_goes_in_the_capture()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        var rejected = new CapturedReply("rejected: Reply padding was not zero.", 2, "00AC");
        _h.Sensor.OnRelease = () => _h.Sensor.Rejected = rejected;

        keyboard.Check.Execute(null);
        _h.Workspace.Session = SessionState.Starting;
        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);

        Assert.Equal([rejected], RejectedIn(_h.Dialogs.SavePath));
    }

    /// <summary>The calibration card can keep the live sensor polling between checks, so it may still hold an earlier check's rejection.</summary>
    [Fact]
    public void A_capture_keeps_only_a_reply_rejected_during_its_own_check()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        var path = _h.File("capture.json");
        _h.Dialogs.SavePath = path;
        _h.Sensor.Rejected = new CapturedReply("rejected: Reply padding was not zero.", 1, "00AA");

        var rest = CheckAtRest(keyboard);
        keyboard.Tick(rest + KeyboardViewModel.CheckTimeoutMs + 1);
        keyboard.Export.Execute(null);
        Assert.Empty(RejectedIn(path));

        // Rejected while waiting for a key press, after readings had come in.
        rest = CheckAtRest(keyboard);
        var during = new CapturedReply("rejected: Reply padding was not zero.", 4, "00BB");
        _h.Sensor.Rejected = during;
        keyboard.Tick(rest + KeyboardViewModel.CheckTimeoutMs + 1);
        keyboard.Export.Execute(null);
        Assert.Equal([during], RejectedIn(path));
    }

    private static List<CapturedReply> RejectedIn(string path) =>
        [.. CaptureExport.FromJson(File.ReadAllText(path), out _)!.Replies.Where(r => r.Label.StartsWith("rejected", StringComparison.Ordinal))];

    [Fact]
    public void A_capture_leaves_out_an_earlier_check_once_the_board_reports_other_firmware()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        var rest = CheckAtRest(keyboard);
        _h.Sensor.Reading = AppHarness.Holding(16, 3000);
        keyboard.Tick(rest + 100);

        // Updated in GG, then plugged back in.
        _h.FirmwareOf = _ => new FirmwareReading("4.17.0", null, null);
        keyboard.OnKeyboards([]);
        keyboard.OnKeyboards([AppHarness.Gen3Info]);
        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);

        var export = CaptureExport.FromJson(File.ReadAllText(_h.Dialogs.SavePath), out var error);
        Assert.Null(error);
        Assert.Equal("4.17.0", export!.Firmware);
        Assert.DoesNotContain(export.Replies, reply => reply.Label is "at rest" or "key held");
    }

    /// <summary>The card lists a capture's fields by hand, so a new field fails here until the card names it too.</summary>
    [Fact]
    public void A_capture_file_holds_only_the_fields_the_card_lists()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        _h.FirmwareOf = _ => new FirmwareReading("1.0", [0x00, 0x31], null);
        var keyboard = Create(consented: [AppHarness.Gen3]);
        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);

        using var json = JsonDocument.Parse(File.ReadAllText(_h.Dialogs.SavePath));
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(["version", "payload"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["app_version", "taken_at", "product_id", "product_name", "firmware", "container_id", "input_report_length", "output_report_length", "replies"],
            payload.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["label", "group", "reply_hex"], payload.GetProperty("replies")[0].EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(0x1630)]
    [InlineData(0x1632)]
    [InlineData(0x1644)]
    [InlineData(0x1646)]
    public void An_unsupported_board_says_so_and_is_never_asked_for_its_sensors_even_with_earlier_consent(int productId)
    {
        var wireless = AppHarness.Gen3Info with { ProductId = (ushort)productId, Name = "Apex Pro TKL Wireless" };
        _h.Boards[0] = wireless;

        var keyboard = Create(consented: [wireless.ContainerId]);

        Assert.Equal("Not supported", keyboard.Boards.Single().Detail);
        Assert.True(keyboard.IsUnsupported);
        Assert.False(keyboard.IsUnverified);
        Assert.False(keyboard.NeedsConsent);
        Assert.False(keyboard.CanCheck);
        Assert.False(_h.Workspace.Board!.CanReadSensors);
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
