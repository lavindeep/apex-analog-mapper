using ApexMapper.App.Composition;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels;
using ApexMapper.App.ViewModels.Calibration;
using ApexMapper.App.ViewModels.Devices;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.App.ViewModels.Tray;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApexMapper.App.Tests.EndToEnd;

/// <summary>
/// Verifies that <see cref="AppCompositionRoot.ConfigureServices"/> produces a
/// valid <see cref="IServiceProvider"/> and that key services resolve without
/// throwing.
///
/// Strategy: build the container from the composition root, then call
/// <see cref="IServiceProvider.GetRequiredService{T}"/> for each service the
/// spec requires.  No WPF Application is started — the composition root falls
/// back to <see cref="StubTrayService"/> for <see cref="ITrayService"/>.
///
/// Note: <see cref="WindowsTaskSchedulerFacade"/> and
/// <see cref="Win32ForegroundProbe"/> resolve correctly as singletons;
/// they only interact with Win32 when their methods are called, not at
/// construction time.  <see cref="HotkeyService"/> similarly does not call
/// NHotkey at construction — so all resolutions are safe on macOS / CI.
/// </summary>
public sealed class AppCompositionRootTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider;

    public AppCompositionRootTests()
    {
        var services = new ServiceCollection();
        AppCompositionRoot.ConfigureServices(services);
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // Async disposal: MappingEngine and InputHost are IAsyncDisposable-only, and
    // the sync ServiceProvider.Dispose() throws for such singletons.
    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // -----------------------------------------------------------------------
    // Services
    // -----------------------------------------------------------------------

    [Fact]
    public void ITrayService_Resolves()
        => _provider.GetRequiredService<ITrayService>().Should().NotBeNull();

    [Fact]
    public void IHotkeyService_Resolves()
        => _provider.GetRequiredService<IHotkeyService>().Should().NotBeNull();

    [Fact]
    public void IForegroundWatcher_Resolves()
        => _provider.GetRequiredService<IForegroundWatcher>().Should().NotBeNull();

    [Fact]
    public void ISupervisorChannel_Resolves()
        => _provider.GetRequiredService<ISupervisorChannel>().Should().NotBeNull();

    [Fact]
    public void ISupervisorChannel_IsTheRealBridge_And_NotConnected_BeforeEnable()
    {
        var channel = _provider.GetRequiredService<ISupervisorChannel>();
        channel.Should().BeOfType<SupervisorChannelBridge>();
        channel.IsConnected.Should().BeFalse("the channel only connects after an explicit enable");
    }

    [Fact]
    public void IPadStateSink_Resolves_To_The_Channel_Slot()
        => _provider.GetRequiredService<ApexMapper.Core.Pipeline.IPadStateSink>()
            .Should().NotBeNull();

    [Fact]
    public void KeyStateStore_Resolves_As_A_Shared_Singleton()
    {
        var a = _provider.GetRequiredService<ApexMapper.Core.Keys.KeyStateStore>();
        var b = _provider.GetRequiredService<ApexMapper.Core.Keys.KeyStateStore>();
        a.Should().BeSameAs(b, "the input host and the engine must share one store");
    }

    [Fact]
    public void InputHost_Resolves()
        => _provider.GetRequiredService<ApexMapper.Input.Abstractions.Hosting.InputHost>()
            .Should().NotBeNull();

    [Fact]
    public void MappingEngine_Resolves_And_Starts_Disabled()
    {
        var engine = _provider.GetRequiredService<ApexMapper.Core.Engine.MappingEngine>();
        engine.IsEnabled.Should().BeFalse("mapping is off until the user's enable flow succeeds");
    }

    [Fact]
    public void IMappingSession_Resolves_Disabled()
    {
        var session = _provider.GetRequiredService<IMappingSession>();
        session.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void DeviceAdapterDescriptor_Resolves_From_The_Embedded_Resource()
    {
        var descriptor = _provider.GetRequiredService<ApexMapper.Input.Abstractions.Adapters.DeviceAdapterDescriptor>();
        descriptor.Match.VendorId.Should().Be(0x1038, "the shipped adapter targets the SteelSeries VID");
    }

    [Fact]
    public void Detection_And_Preflight_Services_Resolve()
    {
        _provider.GetRequiredService<ApexMapper.Output.Preflight.PreflightRunner>().Should().NotBeNull();
        _provider.GetRequiredService<ApexMapper.Output.Detection.AntiCheatDetector>().Should().NotBeNull();
        _provider.GetRequiredService<ApexMapper.Output.Detection.SteamDetector>().Should().NotBeNull();
    }

    [Fact]
    public void ISupervisorProcessLauncher_Resolves()
        => _provider.GetRequiredService<ISupervisorProcessLauncher>().Should().NotBeNull();

    [Fact]
    public void IPanicPolicyStore_Resolves()
        => _provider.GetRequiredService<IPanicPolicyStore>().Should().NotBeNull();

    [Fact]
    public void ITrayProfileSource_Resolves()
        => _provider.GetRequiredService<ITrayProfileSource>().Should().NotBeNull();

    [Fact]
    public void ICalibrationService_Resolves()
        => _provider.GetRequiredService<ICalibrationService>().Should().NotBeNull();

    [Fact]
    public void ILoginTaskService_Resolves()
        => _provider.GetRequiredService<ILoginTaskService>().Should().NotBeNull();

    [Fact]
    public void IDialogService_Resolves()
        => _provider.GetRequiredService<IDialogService>().Should().NotBeNull();

    [Fact]
    public void IProfileHotReload_Resolves()
        => _provider.GetRequiredService<IProfileHotReload>().Should().NotBeNull();

    [Fact]
    public void IAppPaths_Resolves()
        => _provider.GetRequiredService<IAppPaths>().Should().NotBeNull();

    // -----------------------------------------------------------------------
    // ViewModels
    // -----------------------------------------------------------------------

    [Fact]
    public void MainWindowViewModel_Resolves()
        => _provider.GetRequiredService<MainWindowViewModel>().Should().NotBeNull();

    [Fact]
    public void TrayMenuViewModel_Resolves()
        => _provider.GetRequiredService<TrayMenuViewModel>().Should().NotBeNull();

    [Fact]
    public void ProfileSelectorViewModel_Resolves()
        => _provider.GetRequiredService<ProfileSelectorViewModel>().Should().NotBeNull();

    [Fact]
    public void DevicePickerViewModel_Resolves()
        => _provider.GetRequiredService<DevicePickerViewModel>().Should().NotBeNull();

    [Fact]
    public void CalibrationWizardViewModel_Resolves()
        => _provider.GetRequiredService<CalibrationWizardViewModel>().Should().NotBeNull();

    // -----------------------------------------------------------------------
    // Singleton identity: same instance returned on repeated resolution
    // -----------------------------------------------------------------------

    [Fact]
    public void ITrayService_IsSingleton()
    {
        var a = _provider.GetRequiredService<ITrayService>();
        var b = _provider.GetRequiredService<ITrayService>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void ISupervisorChannel_IsSingleton()
    {
        var a = _provider.GetRequiredService<ISupervisorChannel>();
        var b = _provider.GetRequiredService<ISupervisorChannel>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void MainWindowViewModel_ChildViewModels_AreWired()
    {
        var vm = _provider.GetRequiredService<MainWindowViewModel>();
        vm.ProfileSelectorViewModel.Should().NotBeNull();
        vm.DevicePickerViewModel.Should().NotBeNull();
        vm.CalibrationWizardViewModel.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // Pipeline wiring: the engine tick must drain the input ring
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EngineTick_DrainsTheInputRing_Through_ThePreTickHook()
    {
        // The composition root wires the engine's preTick to InputHost.Drain so
        // every tick consumes queued raw input — even while mapping is disabled
        // (a key release must still clear its gate). Enqueue a synthetic event,
        // start the engine, and assert the ring empties within a bounded wait.
        // If the preTick link is dropped (preTick: null) the ring never drains.
        var ring = _provider.GetRequiredService<
            ApexMapper.Input.Abstractions.Pipeline.SpscRingBuffer<
                ApexMapper.Input.Abstractions.Pipeline.RawKeyEvent>>();
        var engine = _provider.GetRequiredService<ApexMapper.Core.Engine.MappingEngine>();

        ring.TryEnqueue(new ApexMapper.Input.Abstractions.Pipeline.RawKeyEvent(
            ScanCode: 0x11, IsDown: true, TimestampTicks: 0, DeviceId: 1))
            .Should().BeTrue("the empty ring must accept the synthetic event");

        await engine.StartAsync(CancellationToken.None);

        // Poll for up to ~2 s (200 × 10 ms) for the tick loop to drain the ring.
        for (var i = 0; i < 200 && !ring.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        ring.IsEmpty.Should().BeTrue("the engine tick's preTick must drain the input ring");
        engine.IsEnabled.Should().BeFalse("draining is independent of the enable state — that is the point");
    }
}
