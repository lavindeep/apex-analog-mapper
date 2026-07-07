using ApexMapper.App.Persistence;
using ApexMapper.App.Services;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Hid;
using ApexMapper.Input.RawInput;
using ApexMapper.Output.Detection;
using ApexMapper.Output.Preflight;
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
    // Embedded adapter descriptor for the Apex Pro family (VID/PID match,
    // interface selection, exploratory analog key map).
    private const string ApexProAdapterResource =
        "ApexMapper.Input.Abstractions.adapters.steelseries-apex-pro-v2.json";

    // Raw-input ring: power-of-two capacity; RawKeyEvent is 16 bytes, so this
    // is 32 KB and covers multi-second bursts at any human typing rate.
    private const int InputRingCapacity = 2048;

    // Upper bound on events drained per 1 ms mapping tick — bounds tick latency
    // while still draining far faster than any keyboard can produce.
    private const int MaxDrainedEventsPerTick = 256;

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
        // Input pipeline (digital raw input + device selection)
        // -----------------------------------------------------------------------

        services.AddSingleton(_ => DeviceAdapterStore.LoadEmbedded(ApexProAdapterResource));

        services.AddSingleton(_ => KeyUniverse.CreateFullIndex());

        // Index-backed store: the only store mode safe for the concurrent
        // adapter/tick-thread access pattern InputHost and MappingEngine use.
        services.AddSingleton(sp => new KeyStateStore(sp.GetRequiredService<KeyIndex>()));

        services.AddSingleton(_ => new SpscRingBuffer<RawKeyEvent>(InputRingCapacity));

        services.AddSingleton<IRawInputAdapter>(sp =>
            new RawInputAdapter(sp.GetRequiredService<SpscRingBuffer<RawKeyEvent>>()));

        services.AddSingleton<DeviceSelector>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            var registryFile = paths.DeviceRegistryFile;
            var descriptor = sp.GetRequiredService<DeviceAdapterDescriptor>();

            return new DeviceSelector(
                new HidSharpDeviceProvider(descriptor),
                loadRegistry:  () => DeviceRegistry.Load(registryFile),
                saveRegistry:  r  => DeviceRegistry.Save(registryFile, r));
        });

        // Digital-only for now: the shipped Apex adapter's key_map is empty (the
        // analog HID protocol is exploratory, pending hardware), so opening an
        // analog probe would poll a stream with zero mapped fields. When the key
        // map gains entries, pass the opened device + descriptor + its input
        // report length here and the persisted calibrations flow in via the
        // factory's registry lookup.
        services.AddSingleton(sp => InputHostFactory.Create(
            rawInput:       sp.GetRequiredService<IRawInputAdapter>(),
            hidDevice:      null,
            adapter:        null,
            reportLength:   0,
            deviceSelector: sp.GetRequiredService<DeviceSelector>(),
            loadRegistry:   () => DeviceRegistry.Load(sp.GetRequiredService<IAppPaths>().DeviceRegistryFile),
            ring:           sp.GetRequiredService<SpscRingBuffer<RawKeyEvent>>(),
            store:          sp.GetRequiredService<KeyStateStore>(),
            log:            new LoggerLogSink(sp.GetRequiredService<ILogger<InputHost>>())));

        // -----------------------------------------------------------------------
        // Mapping engine + session
        // -----------------------------------------------------------------------

        services.AddSingleton(sp =>
        {
            var host = sp.GetRequiredService<InputHost>();
            var engine = new MappingEngine(
                sp.GetRequiredService<KeyStateStore>(),
                sp.GetRequiredService<IPadStateSink>(),
                tickIntervalMs: 1,
                preTick: () => host.Drain(MaxDrainedEventsPerTick));

            // The app starts with mapping OFF; only MappingSession.EnableAsync
            // (a user action behind the fail-closed enable flow) turns it on.
            engine.SetEnabled(false);
            return engine;
        });

        services.AddSingleton<IProcessEnumerator, WindowsProcessEnumerator>();

        services.AddSingleton(sp => new AntiCheatDetector(sp.GetRequiredService<IProcessEnumerator>()));

        services.AddSingleton(sp => new SteamDetector(sp.GetRequiredService<IProcessEnumerator>()));

        services.AddSingleton(_ => new PreflightRunner(new IPreflightCheck[]
        {
            new ViGEmBusPreflightCheck(),
        }));

        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<ProfileStore>();
            var engine = sp.GetRequiredService<MappingEngine>();
            return new ProfileActivationService(
                loadProfiles: () => store.LoadAll(),
                sp.GetRequiredService<IProfileHotReload>(),
                sp.GetRequiredService<IForegroundWatcher>(),
                sp.GetRequiredService<IProfileManualPinStore>(),
                applyProfile: profile => engine.SetProfile(profile),
                sp.GetRequiredService<ILogger<ProfileActivationService>>());
        });

        // Resume guard: on system resume from sleep/hibernate, held keys are
        // gated so a key-up missed while suspended cannot latch an axis. The
        // source binds a static OS event; both singletons are disposed on
        // shutdown so the subscription never leaks.
        services.AddSingleton<IPowerModeSource>(_ => new SystemEventsPowerModeSource());

        services.AddSingleton(sp => new ResumeGuard(
            sp.GetRequiredService<IPowerModeSource>(),
            sp.GetRequiredService<IMappingSession>(),
            sp.GetRequiredService<ILogger<ResumeGuard>>()));

        services.AddSingleton<IMappingSession>(sp => new MappingSession(
            sp.GetRequiredService<KeyStateStore>(),
            sp.GetRequiredService<MappingEngine>(),
            sp.GetRequiredService<ISupervisorChannel>(),
            sp.GetRequiredService<PreflightRunner>(),
            sp.GetRequiredService<AntiCheatDetector>(),
            sp.GetRequiredService<SteamDetector>(),
            sp.GetRequiredService<ISupervisorProcessLauncher>(),
            sp.GetRequiredService<IForegroundWatcher>(),
            confirm: (title, message) => sp.GetRequiredService<IDialogService>().Confirm(title, message),
            sp.GetRequiredService<ILogger<MappingSession>>()));

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

        services.AddSingleton(SupervisorSessionId.Current());

        services.AddSingleton<ISupervisorChannel>(sp => SupervisorClientFactory.Create(sp));

        // The mapping engine's sink is the channel's latest-wins pad-state slot;
        // the bridge owns the adapter, so the sink is exposed through it.
        services.AddSingleton<IPadStateSink>(sp =>
            ((SupervisorChannelBridge)sp.GetRequiredService<ISupervisorChannel>()).Sink);

        services.AddSingleton<ISupervisorProcessLauncher>(sp =>
            new SupervisorProcessLauncher(sp.GetRequiredService<SupervisorSessionId>().Value));

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

        services.AddSingleton<ITaskSchedulerFacade, WindowsTaskSchedulerFacade>();

        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new LoginTaskOptions(
                ExecutablePath: paths.ExecutablePath,
                TaskName:       "ApexProAnalogMapper",
                Description:    "Apex Analog Mapper — start at user login");
        });

        services.AddSingleton<ILoginTaskService>(sp =>
            new LoginTaskService(
                sp.GetRequiredService<ITaskSchedulerFacade>(),
                sp.GetRequiredService<LoginTaskOptions>()));

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
            // resolveCurrentId: the activation service owns the resolved id.
            // Resolved lazily per call — no construction-order dependency.
            return new ProfileSelectorViewModel(
                store,
                pinStore,
                resolveCurrentId: () => sp.GetRequiredService<ProfileActivationService>().CurrentProfileId);
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
                sp.GetRequiredService<PanicCoordinator>(),
                sp.GetRequiredService<IMappingSession>()));

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
