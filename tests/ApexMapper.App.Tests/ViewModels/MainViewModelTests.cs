using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Keys;
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
}
