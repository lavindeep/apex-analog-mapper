using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class MainViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Ticks the window as its timer would until <paramref name="ms"/> of the harness clock have passed.</summary>
    private void TickFor(MainViewModel main, int ms)
    {
        var end = _h.Now + ms;
        while (_h.Now < end)
        {
            _h.Now += MainViewModel.FastTickMs;
            main.Tick();
        }
    }

    [Fact]
    public void The_launch_check_follows_the_saved_settings()
    {
        _ = new MainViewModel(_h.Services, _h.Workspace, new AppSettings(CheckForUpdates: false));

        Assert.Empty(_h.Updates.Asked);
    }

    [Fact]
    public void The_calibration_link_shows_the_calibration_page_and_speeds_up_the_timer()
    {
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        Assert.Equal(MainViewModel.SlowTickMs, main.TickIntervalMs);

        main.Status.GoToCalibration.Execute(null);

        Assert.Equal(Page.Calibration, main.Page);
        Assert.True(main.Calibration.IsOpen);
        Assert.Equal(MainViewModel.FastTickMs, main.TickIntervalMs);

        main.GoHome.Execute(null);

        Assert.False(main.Calibration.IsOpen);
        Assert.Equal(MainViewModel.SlowTickMs, main.TickIntervalMs);
    }

    [Fact]
    public void Leaving_the_profile_page_ends_a_key_capture()
    {
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        main.OpenProfile.Execute(null);
        Assert.True(main.Profile.IsOpen);
        main.Profile.AddKey.Execute(null);
        Assert.True(main.Profile.IsCapturing);

        main.GoHome.Execute(null);

        Assert.False(main.Profile.IsCapturing);
        Assert.False(main.Profile.IsOpen);
    }

    [Fact]
    public void Leaving_the_keyboard_page_or_the_window_ends_a_sensor_check()
    {
        _h.Boards[0] = AppHarness.Gen3Info;
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings(ConsentedKeyboards: [AppHarness.Gen3]));
        main.Keyboard.OnKeyboards(_h.Boards);
        main.Page = Page.Keyboard;
        main.Keyboard.Check.Execute(null);
        Assert.True(main.Keyboard.IsChecking);

        main.GoHome.Execute(null);

        Assert.False(main.Keyboard.IsChecking);
        Assert.Equal("The check stopped when you left the Keyboard page. Run it again.", main.Keyboard.CheckText);

        main.Page = Page.Keyboard;
        main.Keyboard.Check.Execute(null);
        main.OnDeactivated();

        Assert.False(main.Keyboard.IsChecking);
        Assert.Equal("The check stopped when you switched to another window. Run it again.", main.Keyboard.CheckText);
    }

    [Fact]
    public void A_tick_hands_key_presses_to_key_capture()
    {
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        main.Profile.AddKey.Execute(null);

        _h.Keys.Press(new ScanCode(0x25));
        main.Tick();

        Assert.False(main.Profile.IsCapturing);
        Assert.Equal(new ScanCode(0x25), main.Profile.SelectedRow!.Key);
    }

    [Fact]
    public void The_status_card_shows_the_chosen_games_warning_until_another_game_is_chosen()
    {
        _h.Windows =
        [
            new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Elevated),
            new GameWindow("Untitled - Notepad", @"C:\Windows
otepad.exe", Elevation.Visible),
        ];
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());

        main.Game.Selected = main.Game.Games[0];
        Assert.Equal([main.Game.Warning!], main.Status.Warnings);

        main.Game.Selected = main.Game.Games[1];
        Assert.Empty(main.Status.Warnings);
    }

    [Fact]
    public async Task A_tick_hands_key_presses_to_the_other_keyboard_notice()
    {
        _h.CalibrateForza();
        _h.Keyboards.Refresh();
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings(GamePath: AppHarness.Game));
        await main.Status.StartAsync();
        _h.Keys.Containers[2] = Guid.NewGuid();

        _h.Keys.Press(DefaultProfiles.Key.W, device: 2);
        main.Tick();

        Assert.Contains(main.Status.Notes, w => w.Contains("another keyboard", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_whose_release_went_missing_is_forgotten_when_the_window_comes_back()
    {
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        var key = new ScanCode(0x25);
        _h.Keys.Press(key);
        main.Tick();

        main.OnActivated();
        main.Profile.AddKey.Execute(null);
        _h.Keys.Press(key);
        main.Tick();

        Assert.False(main.Profile.IsCapturing);
        Assert.Equal(key, main.Profile.SelectedRow!.Key);
    }

    [Fact]
    public void A_tick_hands_keys_to_calibration_and_coming_back_to_the_front_forgets_a_held_one()
    {
        _h.Keyboards.Refresh();
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        main.Calibration.IsOpen = true;
        var w = main.Calibration.Rows.Single(r => r.Key == DefaultProfiles.Key.W);
        _h.Sensor.Reading = FakeSensor.AtRest();

        w.SetReleased.Execute(null);
        _h.Keys.Press(DefaultProfiles.Key.W);
        TickFor(main, CalibrationViewModel.ReleasedMs);
        Assert.StartsWith("W was pressed", w.Message);

        // W came up behind the lock screen, so its key-up never came.
        main.OnActivated();
        w.SetReleased.Execute(null);
        TickFor(main, CalibrationViewModel.ReleasedMs);
        Assert.StartsWith("Released 850", w.Message);
    }

    [Fact]
    public void Coming_back_to_the_front_checks_the_driver_and_looks_for_the_game_again()
    {
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        Assert.Equal(1, _h.WindowScans);
        _h.DriverCheck = () => DriverState.Missing;
        _h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];

        main.OnActivated();

        Assert.Equal(DriverState.Missing, _h.Workspace.Driver);
        Assert.Equal(2, _h.WindowScans);
        Assert.Single(main.Game.Games);
    }
}
