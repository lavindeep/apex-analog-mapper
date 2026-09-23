using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApexMapper.App.Storage;
using ApexMapper.App.Tests.ViewModels;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Profiles;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Session;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace ApexMapper.App.Tests.Views;

/// <summary>
/// Shows the real window offscreen, over fake services, in a few states and in light
/// and dark, and fails on any binding WPF could not resolve. Nothing reaches a keyboard
/// or the driver. Set APEX_SCREENSHOTS to a folder to keep an image of every card.
/// </summary>
public sealed class WindowRenderingTests
{
    private const double Scale = 1.5;

    /// <summary>Long enough for WPF-UI's expand and open animations to finish.</summary>
    private const int AnimationsMs = 800;

    private sealed record Scenario(string Name, Action<AppHarness> Arrange, Func<MainViewModel, AppHarness, Task>? Act = null);

    private static readonly Scenario[] Scenarios =
    [
        new("first-run", h => h.Boards.Clear()),
        new("calibrating", h =>
        {
            h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
            h.Services.Calibrations.Put(AppHarness.Tkl, AppHarness.Firmware, DefaultProfiles.Key.W, KeyCalibration.Create(850, 4095, 40, 16));
            h.Sensor.Reading = AppHarness.Holding(16, 2400);
        }, (main, _) =>
        {
            main.Profile.IsOpen = true;
            main.Calibration.IsOpen = true;
            main.Tick();
            return Task.CompletedTask;
        }),
        new("mapping", h =>
        {
            h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
            h.CalibrateForza();
        }, async (main, h) =>
        {
            await main.Status.StartAsync();
            var status = new SessionStatus(SessionState.Running, null, GameRunning: false, GameHasFocus: false, GameElevated: false, FallbackKeys: 1,
                SensorProblem: "The keyboard stopped answering.", KeysAwaitingRelease: false, SubmitCount: 0, HookReinstalls: 1, RestartRequired: false,
                CycleP50Ms: 1.5f, CycleP99Ms: 2.9f, KeysAtLimit: [DefaultProfiles.Key.S]);
            for (var ms = 0; ms <= StatusViewModel.AtLimitWarningMs; ms += 500)
            {
                h.Session.NextStatus = status with { SubmitCount = ms / 2 };
                main.Status.Tick(h.Now + ms);
            }
        }),
        new("untested-board", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }),
    ];

    [Fact]
    public void Every_card_shows_with_no_binding_errors_in_light_and_dark()
    {
        var folder = Environment.GetEnvironmentVariable("APEX_SCREENSHOTS");
        var errors = OnStaThread(() => RenderAll(folder));
        Assert.Empty(errors);
    }

    private static List<string> RenderAll(string? folder)
    {
        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        // As App.xaml does it: the theme manager looks for the theme one level down.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/ApexAnalogMapper;component/Views/Resources.xaml") };
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
                foreach (var scenario in Scenarios)
                {
                    errors.Scenario = $"{scenario.Name}, {theme}";
                    Render(scenario, theme, folder);
                }
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            app.Shutdown();
        }
        return errors.Messages;
    }

    private static void Render(Scenario scenario, ApplicationTheme theme, string? folder)
    {
        using var h = new AppHarness();
        scenario.Arrange(h);
        h.Keyboards.Refresh();
        var main = new MainViewModel(h.Services, h.Workspace, new AppSettings(GamePath: AppHarness.Game));
        if (scenario.Act is { } act)
        {
            // Everything the fakes do completes at once, so this never waits.
            act(main, h).GetAwaiter().GetResult();
        }
        var window = new MainWindow
        {
            Left = -20000,
            Top = -20000,
            Height = 6000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show(main);
        try
        {
            foreach (var expander in Descendants<CardExpander>(window))
            {
                expander.IsExpanded = true;
            }
            Settle(AnimationsMs);
            if (folder is null)
            {
                return;
            }
            Directory.CreateDirectory(folder);
            var background = (Brush)Application.Current.Resources["ApplicationBackgroundBrush"];
            var column = (System.Windows.Controls.Panel)Descendants<System.Windows.Controls.ScrollViewer>(window).First().Content;
            var index = 0;
            foreach (FrameworkElement card in column.Children)
            {
                index++;
                var name = $"{scenario.Name}-{theme.ToString().ToLowerInvariant()}-{index}-{card.GetType().Name}.png";
                Save(Snapshot(card, background), Path.Combine(folder, name));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Runs the dispatcher for a while: layout, bindings, Loaded handlers, and the expand and open animations.</summary>
    private static void Settle(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The element alone, at its laid-out size, over the window background.</summary>
    private static BitmapSource Snapshot(FrameworkElement element, Brush background)
    {
        var size = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        // The brush draws the element where its parent placed it, and draws whatever overhangs
        // its layout slot, so the view box picks out the slot itself.
        var slot = new Rect((Point)VisualTreeHelper.GetOffset(element), size.Size);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(background, null, size);
            context.DrawRectangle(new VisualBrush(element) { Viewbox = slot, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None }, null, size);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result!;
    }

    /// <summary>Binding errors and warnings WPF reports while the window shows.</summary>
    private sealed class BindingErrors : TraceListener
    {
        public string Scenario { get; set; } = "";

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add($"{Scenario}: {message}");
            }
        }
    }
}
