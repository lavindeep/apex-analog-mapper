using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Windows.Output;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class SetupViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void A_missing_driver_opens_the_card_and_offers_its_release_page_until_it_is_installed()
    {
        _h.DriverCheck = () => DriverState.Missing;
        var setup = new SetupViewModel(_h.Services, _h.Workspace, new AppSettings());

        Assert.True(setup.IsOpen);
        Assert.Equal("The controller driver is not installed", setup.Summary);
        Assert.Equal(DriverState.Missing, _h.Workspace.Driver);
        Assert.True(setup.DriverMissing);
        Assert.Contains(SetupViewModel.DriverVersion, setup.DriverText);
        setup.OpenDriverPage.Execute(null);
        Assert.Equal([SetupViewModel.DriverPage], _h.Opened);

        _h.DriverCheck = () => DriverState.NotStarted;
        setup.Recheck();
        Assert.False(setup.DriverMissing);
        Assert.False(setup.DriverReady);
        Assert.Contains("Restart the PC", setup.DriverText);

        _h.DriverCheck = () => DriverState.Running;
        setup.Recheck();
        Assert.True(setup.DriverReady);
        Assert.Equal("Controller driver, Steam, updates, and where the app keeps its files", setup.Summary);
        Assert.Equal(DriverState.Running, _h.Workspace.Driver);
    }

    [Fact]
    public void A_running_driver_leaves_the_card_closed()
    {
        var setup = new SetupViewModel(_h.Services, _h.Workspace, new AppSettings());

        Assert.False(setup.IsOpen);
    }

    [Fact]
    public void A_driver_check_that_throws_reads_as_unknown()
    {
        _h.DriverCheck = () => throw new InvalidOperationException("The service manager is not available.");

        var setup = new SetupViewModel(_h.Services, _h.Workspace, new AppSettings());

        Assert.False(setup.DriverMissing);
        Assert.False(setup.DriverReady);
        Assert.StartsWith("Windows would not say", setup.DriverText);
    }
}
