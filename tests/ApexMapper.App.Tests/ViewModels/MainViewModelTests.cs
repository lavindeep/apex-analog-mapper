using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class MainViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void The_calibration_link_opens_the_calibration_card_and_speeds_up_the_timer()
    {
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings());
        object? requested = null;
        main.CardRequested += card => requested = card;
        Assert.Equal(MainViewModel.SlowTickMs, main.TickIntervalMs);

        main.Status.GoToCalibration.Execute(null);

        Assert.True(main.Calibration.IsOpen);
        Assert.Same(main.Calibration, requested);
        Assert.Equal(MainViewModel.FastTickMs, main.TickIntervalMs);
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
    public async Task A_tick_hands_key_presses_to_the_other_keyboard_notice()
    {
        _h.CalibrateForza();
        _h.Keyboards.Refresh();
        var main = new MainViewModel(_h.Services, _h.Workspace, new AppSettings(GamePath: AppHarness.Game));
        await main.Status.StartAsync();
        _h.Keys.Containers[2] = Guid.NewGuid();

        _h.Keys.Press(DefaultProfiles.Key.W, device: 2);
        main.Tick();

        Assert.Contains(main.Status.Warnings, w => w.Contains("another keyboard", StringComparison.Ordinal));
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
