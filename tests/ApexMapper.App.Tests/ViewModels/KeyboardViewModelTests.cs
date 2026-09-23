using System.IO;
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

    private KeyboardViewModel Create(Guid? remembered = null, IEnumerable<Guid>? consented = null)
    {
        var keyboard = new KeyboardViewModel(_h.Services, _h.Workspace, remembered, consented);
        keyboard.OnKeyboards(_h.Boards);
        return keyboard;
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
    public void A_choice_among_several_boards_is_remembered()
    {
        _h.Boards.Add(AppHarness.Gen3Info);
        var keyboard = Create();
        Assert.Null(keyboard.Selected);
        Assert.Equal("Choose your keyboard.", keyboard.Missing);
        Assert.Null(_h.Workspace.Board);

        keyboard.Selected = keyboard.Boards.Single(b => b.Id == AppHarness.Gen3);

        Assert.Equal(AppHarness.Gen3, _h.SavedSettings.Keyboard);
        Assert.Equal(AppHarness.Gen3, _h.Workspace.Board?.Id);
        Assert.Null(keyboard.Missing);
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
        Assert.Contains("4.20.0", keyboard.FirmwareWarning);
        Assert.True(_h.Workspace.Board!.CanReadSensors);
    }

    [Fact]
    public void An_untested_board_is_labelled_and_its_sensors_are_not_read_until_the_user_agrees()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create();

        Assert.True(keyboard.IsUnverified);
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

        keyboard.Check.Execute(null);
        _h.Sensor.Reading = FakeSensor.AtRest();
        keyboard.Tick(_h.Now);
        _h.Sensor.Reading = AppHarness.Holding(16, 3000);
        keyboard.Tick(_h.Now + 100);

        Assert.False(keyboard.IsChecking);
        Assert.StartsWith("The sensors respond to key presses.", keyboard.CheckText);
        Assert.False(_h.Sensor.Wanted);

        _h.Dialogs.SavePath = _h.File("capture.json");
        keyboard.Export.Execute(null);

        var export = CaptureExport.FromJson(File.ReadAllText(_h.Dialogs.SavePath), out var error);
        Assert.Null(error);
        Assert.Equal(0x1642, export!.ProductId);
        Assert.Equal("1.0", export.Firmware);
        Assert.Equal("00312E30", export.Replies.Single(r => r.Label == "firmware").ReplyHex);
        Assert.Equal(5, export.Replies.Count(r => r.Label == "at rest"));
        var held = export.Replies.Single(r => r.Label == "key held" && r.Group == 2);
        var raw = new ushort[SensorProtocol.SensorsPerGroup];
        Assert.Null(SensorProtocol.ParseGroup(Convert.FromHexString(held.ReplyHex), raw, new ushort[SensorProtocol.SensorsPerGroup]));
        Assert.Equal(3000, raw[2]);
    }

    [Fact]
    public void The_check_asks_for_a_capture_when_nothing_moves()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var keyboard = Create(consented: [AppHarness.Gen3]);
        keyboard.Check.Execute(null);
        _h.Sensor.Reading = FakeSensor.AtRest();

        keyboard.Tick(_h.Now);
        keyboard.Tick(_h.Now + KeyboardViewModel.CheckTimeoutMs);
        Assert.True(keyboard.IsChecking);
        keyboard.Tick(_h.Now + KeyboardViewModel.CheckTimeoutMs + 1);

        Assert.False(keyboard.IsChecking);
        Assert.StartsWith("No sensor moved", keyboard.CheckText);
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
