using System.Windows;
using System.Windows.Threading;
using ApexMapper.App.Composition;
using ApexMapper.App.Services;
using ApexMapper.App.SingleInstance;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApexMapper.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceGuard? _guard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ------------------------------------------------------------------
        // 1. Single-instance guard
        // ------------------------------------------------------------------
        _guard = new SingleInstanceGuard();
        if (!_guard.IsPrimary)
        {
            MessageBox.Show(
                "Apex Mapper is already running.",
                "Apex Mapper",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // ------------------------------------------------------------------
        // 2. Unhandled exception handlers
        // ------------------------------------------------------------------
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;

        // ------------------------------------------------------------------
        // 3. Build the TaskbarIcon from the XAML resource dictionary.
        //    This must happen before building the host so the live TrayService
        //    can be injected via TrayServiceHolder.
        // ------------------------------------------------------------------
        var trayIcon    = (TaskbarIcon)Resources["ApexMapperTrayIcon"];
        var trayService = new TrayService(trayIcon);

        // ------------------------------------------------------------------
        // 4. Build host / DI container
        // ------------------------------------------------------------------
        // Pre-create the holder so the composition root's ITrayService factory
        // sees the live TrayService when the container is first built.
        var trayHolder = new TrayServiceHolder { Value = trayService };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, svc) =>
            {
                AppCompositionRoot.ConfigureServices(svc);

                // Replace the holder registered by ConfigureServices with our
                // pre-populated instance so ITrayService resolves the real service.
                svc.AddSingleton(trayHolder);
            })
            .Build();

        // Bind the tray context menu VM to the icon.
        var trayMenuVm = _host.Services.GetRequiredService<ViewModels.Tray.TrayMenuViewModel>();
        trayIcon.DataContext = trayMenuVm;

        // Start the global panic hotkey (Ctrl+Alt+F12 by default).
        var coordinator = _host.Services.GetRequiredService<PanicCoordinator>();
        coordinator.Start(new HotkeyGesture(
            System.Windows.Input.Key.F12,
            System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt));

        // Surface panic failures to the user: without this the fail-closed panic
        // path is silent (all callers fire-and-forget). Either error slot means the
        // panic did not fully complete. PanicCompleted fires on whatever thread ran
        // the panic (the hotkey path uses Task.Run), and the tray icon is a WPF
        // object — marshal onto the dispatcher or the balloon itself would throw.
        coordinator.PanicCompleted += (_, args) =>
        {
            if (args.Error is not null || args.PolicyError is not null)
                Dispatcher.InvokeAsync(() =>
                    trayService.ShowBalloon("Apex Mapper", "Panic did not fully complete — check that the mapper is still active."));
        };

        // Start the foreground watcher on the UI thread: its WinEvent hook needs
        // this thread's message pump, and without Start() the panic policy leg would
        // never see the active game (Current stays ForegroundContext.Empty). The host
        // owns the singleton and disposes it (Stop + unhook) on shutdown.
        _host.Services.GetRequiredService<IForegroundWatcher>().Start();

        // Start watching the profiles directory for on-disk edits; without Start()
        // no FileSystemWatcher is created and hot-reload never happens. The host
        // disposes the singleton (stopping the watcher) on shutdown.
        _host.Services.GetRequiredService<IProfileHotReload>().Start();

        // Set the DataContext on the main window from DI.
        var mainWindowVm = _host.Services.GetRequiredService<ViewModels.MainWindowViewModel>();
        if (MainWindow is not null)
            MainWindow.DataContext = mainWindowVm;

        // ------------------------------------------------------------------
        // 5. Show the tray icon
        // ------------------------------------------------------------------
        trayService.Show();

        // Wire exit / open-window from tray icon events.
        trayService.OpenMainWindowRequested += (_, _) =>
        {
            MainWindow?.Show();
            MainWindow?.Activate();
        };
        trayService.ExitRequested += (_, _) => Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _guard?.Dispose();
        base.OnExit(e);
    }

    // -------------------------------------------------------------------------
    // Unhandled exception handlers
    // -------------------------------------------------------------------------

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (_host is not null)
                _host.Services.GetRequiredService<PanicCoordinator>()
                     .PanicAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort — do not mask the original crash.
        }
        // Do not swallow — let the runtime decide based on IsTerminating.
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            if (_host is not null)
                _host.Services.GetRequiredService<PanicCoordinator>()
                     .PanicAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort — do not mask the original exception.
        }
        // Do not set e.Handled = true — let WPF follow its default behaviour.
    }
}
