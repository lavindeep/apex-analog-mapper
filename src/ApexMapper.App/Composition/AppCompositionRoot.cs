using ApexMapper.App.Persistence;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels;
using ApexMapper.App.ViewModels.Calibration;
using ApexMapper.App.ViewModels.Devices;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.App.ViewModels.Tray;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Persistence.Devices;
using ApexMapper.Persistence.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Composition;

/// <summary>
/// Static composition root for the Apex Mapper application.
/// Call <see cref="ConfigureServices"/> from <c>App.xaml.cs</c> to populate
/// the DI container before building the host.
/// </summary>
public static class AppCompositionRoot
{
    /// <summary>
    /// Registers all application services, view-models, and infrastructure
    /// into <paramref name="services"/>.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        // -----------------------------------------------------------------------
        // Infrastructure
        // -----------------------------------------------------------------------

        services.AddLogging();

        // TrayServiceHolder: a carrier that lets App.xaml.cs inject the live
        // TrayService (which needs a WPF TaskbarIcon) after resources are loaded.
        // Registered here so tests can optionally override it; defaults to null
        // (falls back to StubTrayService when Value is not set).
        services.AddSingleton<TrayServiceHolder>();

        services.AddSingleton<IAppPaths, DefaultAppPaths>();

        // -----------------------------------------------------------------------
        // Persistence helpers
        // -----------------------------------------------------------------------

        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new ProfileStoreOptions(paths.ProfilesDirectory);
        });

        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<ProfileStoreOptions>();
            return new ProfileStore(opts);
        });

        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new PanicPolicyOptions(paths.PanicPolicyDirectory);
        });

        // -----------------------------------------------------------------------
        // Core Phase-2 types
        // -----------------------------------------------------------------------

        // IDeviceEnumerator is not yet registered — placeholder for Phase 3.
        // DeviceSelector requires it; registered below via a factory that
        // injects a stub enumerator until the real one arrives.
        services.AddSingleton<DeviceSelector>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            var registryFile = paths.DeviceRegistryFile;

            // Stub enumerator: returns an empty list until Phase 3 wires the real adapter.
            IDeviceEnumerator enumerator = new StubDeviceEnumerator();

            return new DeviceSelector(
                enumerator,
                loadRegistry:  () => DeviceRegistry.Load(registryFile),
                saveRegistry:  r  => DeviceRegistry.Save(registryFile, r));
        });

        // -----------------------------------------------------------------------
        // Services — Phase 4
        // -----------------------------------------------------------------------

        // TrayService / ITrayServiceInternal share the same singleton instance.
        // TrayService requires a TaskbarIcon constructed from the XAML resource
        // dictionary; this is done in App.xaml.cs after the WPF Application
        // resources are loaded.  We register a factory that defers construction.
        // The factory is intentionally left as a placeholder comment:
        //   services.AddSingleton<TrayService>(...) — done in App.xaml.cs.
        // For DI resolution tests the factory below provides a no-op stand-in
        // when the App ResourceDictionary is not loaded.

        services.AddSingleton<IHotkeyService, HotkeyService>();

        services.AddSingleton<IWindowEventSource, WindowEventSink>();
        services.AddSingleton<IForegroundProbe, Win32ForegroundProbe>();
        services.AddSingleton<IForegroundWatcher>(sp =>
            new ForegroundWatcher(
                sp.GetRequiredService<IWindowEventSource>(),
                sp.GetRequiredService<IForegroundProbe>()));

        services.AddSingleton<ISupervisorChannel>(sp => SupervisorClientFactory.Create(sp));

        services.AddSingleton<IPanicPolicyStore>(sp =>
            new JsonPanicPolicyStore(sp.GetRequiredService<PanicPolicyOptions>()));

        services.AddSingleton<PanicCoordinator>();

        services.AddSingleton<IProfileManualPinStore>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new JsonProfileManualPinStore(paths.ProfilePinDirectory);
        });

        services.AddSingleton<IDeviceSelectorFacade>(sp =>
            new DeviceSelectorFacade(sp.GetRequiredService<DeviceSelector>()));

        services.AddSingleton<IDeviceRegistryFacade>(sp =>
            new DeviceRegistryFacade(sp.GetRequiredService<IAppPaths>()));

        // CalibrationService requires IHidAnalogProbe which is not available until
        // Phase 3 wires InputHost.  For now we register a stub that throws on use.
        services.AddSingleton<ICalibrationService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StubCalibrationService>>();
            return new StubCalibrationService(logger);
        });

        services.AddSingleton<ILoginTaskService, LoginTaskService>();
        services.AddSingleton<ITaskSchedulerFacade, WindowsTaskSchedulerFacade>();

        services.AddSingleton<IDialogService, WpfDialogService>();

        services.AddSingleton<IProfileHotReload>(sp =>
        {
            var paths   = sp.GetRequiredService<IAppPaths>();
            var store   = sp.GetRequiredService<ProfileStore>();
            var logger  = sp.GetRequiredService<ILogger<ProfileHotReload>>();
            var options = new ProfileHotReloadOptions(paths.ProfilesDirectory);
            return new ProfileHotReload(store, options, logger);
        });

        // ITrayProfileSource is wired after ViewModels because it depends on
        // ProfileSelectorViewModel — registered below.

        // -----------------------------------------------------------------------
        // ViewModels
        // -----------------------------------------------------------------------

        services.AddSingleton<ProfileSelectorViewModel>(sp =>
        {
            var store      = sp.GetRequiredService<ProfileStore>();
            var pinStore   = sp.GetRequiredService<IProfileManualPinStore>();
            // resolveCurrentId: returns null until a profile is auto-selected.
            return new ProfileSelectorViewModel(store, pinStore, resolveCurrentId: () => null);
        });

        services.AddSingleton<ITrayProfileSource>(sp =>
            new TrayProfileSourceAdapter(sp.GetRequiredService<ProfileSelectorViewModel>()));

        // ITrayService / ITrayServiceInternal:
        // TrayService needs a TaskbarIcon (WPF UI object).  We register a
        // deferred-construction singleton: the first time ITrayService is
        // resolved inside App.xaml.cs we build it from the resource dictionary.
        // For the E2E composition tests we use a stub so DI validation does not
        // require a live WPF Application.
        services.AddSingleton<ITrayService>(sp =>
        {
            // In production App.xaml.cs overrides this registration before
            // building the service provider.  If resolved in the test harness
            // (no WPF pump) a StubTrayService is returned instead.
            return sp.GetService<TrayServiceHolder>()?.Value
                   ?? (ITrayService)new StubTrayService();
        });

        services.AddSingleton<ITrayServiceInternal>(sp =>
        {
            var svc = sp.GetRequiredService<ITrayService>();
            // In production this will be the real TrayService (which implements both).
            // In tests it will be the StubTrayService (same).
            return (ITrayServiceInternal)svc;
        });

        services.AddSingleton<TrayMenuViewModel>(sp =>
            new TrayMenuViewModel(
                sp.GetRequiredService<ITrayServiceInternal>(),
                sp.GetRequiredService<ITrayProfileSource>(),
                sp.GetRequiredService<ISupervisorChannel>(),
                sp.GetRequiredService<PanicCoordinator>()));

        services.AddSingleton<DevicePickerViewModel>(sp =>
            new DevicePickerViewModel(
                sp.GetRequiredService<IDeviceSelectorFacade>(),
                sp.GetRequiredService<IDeviceRegistryFacade>()));

        services.AddSingleton<CalibrationWizardViewModel>(sp =>
        {
            var calibrationService = sp.GetRequiredService<ICalibrationService>();
            // Default wizard options: no specific device pre-selected.
            var options = new CalibrationWizardOptions(Guid.Empty);
            return new CalibrationWizardViewModel(calibrationService, options);
        });

        services.AddSingleton<MainWindowViewModel>();
    }
}
