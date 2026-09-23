using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ApexMapper.App.Logging;
using ApexMapper.App.Model;
using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Session;
using Wpf.Ui.Appearance;

namespace ApexMapper.App;

/// <summary>
/// Composition. Builds the app's long-lived parts in order, gives the window its view
/// models and timer, and takes the parts down in reverse on exit, so the session stops
/// and the game sees the controller at rest before anything it depends on goes. An
/// error on the UI thread is logged, stops the session and closes the app with a
/// message; one on another thread ends the process, and the session's crash guard puts
/// the controller at rest first.
/// </summary>
public partial class App : Application
{
    private readonly SingleInstance _instance;
    private readonly List<IDisposable> _parts = [];
    private AppPaths? _paths;
    private FileLog? _log;
    private MappingSession? _session;
    private DispatcherTimer? _timer;
    private bool _failed;

    public App(SingleInstance instance) => _instance = instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledElsewhere;
        try
        {
            Compose();
        }
        catch (Exception ex)
        {
            Fail("The app could not start.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        for (var i = _parts.Count - 1; i >= 0; i--)
        {
            try
            {
                _parts[i].Dispose();
            }
            catch (Exception ex)
            {
                _log?.Write($"Closing {_parts[i].GetType().Name} failed: {ex}");
            }
        }
        _log?.Write("Closed.");
        _log?.Dispose();
        base.OnExit(e);
    }

    private void Compose()
    {
        var paths = _paths = AppPaths.ForCurrentUser();
        Directory.CreateDirectory(paths.Root);
        var log = _log = new FileLog(paths.Log);
        var version = VersionText();
        log.Write($"Apex Analog Mapper {version} started.");

        var settingsStore = new SettingsStore(paths.Settings);
        var (settings, settingsProblem) = settingsStore.Load();
        if (settingsProblem is not null)
        {
            log.Write(settingsProblem);
        }

        var pump = Own(new RawInputPump());
        pump.Start();
        var keyboards = Own(new KeyboardDiscovery());
        try
        {
            keyboards.Watch(pump);
        }
        catch (Exception ex)
        {
            // Watching is set up before the first listing, so the next device change tries again.
            log.Write("Listing the keyboards failed: " + ex.Message);
        }
        var power = Own(new PowerNotifier());
        _session = Own(new MappingSession(new SessionServices { Keyboards = keyboards, RawInput = pump, Power = power }));
        var workspace = new Workspace { SettingsProblem = settingsProblem };
        var sensor = Own(new LiveSensor(workspace));

        var window = new MainWindow();
        SystemThemeWatcher.Watch(window);
        var services = new AppServices
        {
            Session = _session,
            Keyboards = keyboards,
            Sensor = sensor,
            KeyEvents = new RawInputKeyEvents(pump),
            Profiles = new ProfileStore(paths.Profiles),
            Calibrations = new CalibrationStore(paths.Calibration),
            Settings = settingsStore,
            Dialogs = new WindowDialogs(window),
            Post = action => Dispatcher.BeginInvoke(action),
            Open = Open,
            Restart = Restart,
            Log = log.Write,
            AppVersion = version,
            DataFolder = paths.Root,
        };
        var main = new MainViewModel(services, workspace, settings);
        window.Show(main);
        MainWindow = window;
        _instance.OnShowRequested(() => Dispatcher.BeginInvoke(() => window.BringForward()));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(main.TickIntervalMs) };
        _timer.Tick += (_, _) =>
        {
            main.Tick();
            _timer.Interval = TimeSpan.FromMilliseconds(main.TickIntervalMs);
        };
        _timer.Start();
    }

    private T Own<T>(T part) where T : IDisposable
    {
        _parts.Add(part);
        return part;
    }

    /// <summary>Opens a web page or a folder in the shell.</summary>
    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log?.Write($"Opening {target} failed: {ex.Message}");
        }
    }

    /// <summary>Starts a new copy, which waits for this one to exit, and closes this one.</summary>
    private void Restart()
    {
        _log?.Write("Restarting to clear a virtual controller that could not be removed.");
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Program.RestartedArgument))?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log?.Write("Restart failed: " + ex.Message);
            return;
        }
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Fail("The app hit an error it cannot recover from and will close.", e.Exception);
    }

    private void OnUnhandledElsewhere(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Write("Unhandled error: " + e.ExceptionObject);
        _log?.Dispose();
    }

    private void Fail(string what, Exception e)
    {
        if (_failed)
        {
            return;
        }
        _failed = true;
        _timer?.Stop();
        _log?.Write($"{what} {e}");
        try
        {
            // The controller goes to rest and is unplugged before the dialog waits for the user.
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Write("Stopping the session failed: " + ex);
        }
        var details = _paths is { } paths ? $"\n\nThe log has the details: {paths.Log}" : "";
        System.Windows.MessageBox.Show($"{what}\n\n{e.Message}{details}", "Apex Analog Mapper", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private static string VersionText() =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
