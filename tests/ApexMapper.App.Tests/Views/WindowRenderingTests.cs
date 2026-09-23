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
using ApexMapper.Windows.Output;
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

    /// <summary>Long enough for WPF-UI's expand and open animations to finish, which only images need.</summary>
    private const int AnimationsMs = 800;

    /// <summary>Long enough for layout and every binding to resolve.</summary>
    private const int BindingsMs = 50;

    /// <param name="Narrow">Shown at the window's minimum width.</param>
    private sealed record Scenario(string Name, Action<AppHarness> Arrange, Func<MainViewModel, AppHarness, Task>? Act = null, bool Narrow = false);

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
            var status = new SessionStatus(SessionState.Running, null, GameRunning: false, GameHasFocus: false, GameElevation: Elevation.Visible, FallbackKeys: 1,
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
        new("driver-missing", h => h.DriverCheck = () => DriverState.Missing),
        new("restart-required", h =>
        {
            h.CalibrateForza();
            h.Session.RestartRequired = true;
            h.Session.LastEnd = SessionEnd.For(EndReason.EngineStalled);
        }),
        new("axis-capture", _ => { }, (main, _) =>
        {
            main.Profile.IsOpen = true;
            main.Profile.SelectedRow = main.Profile.Rows.First(r => r.IsAxis);
            main.Profile.CaptureNegativeKey.Execute(null);
            return Task.CompletedTask;
        }, Narrow: true),
    ];

    [Fact]
    public void Every_card_shows_with_no_binding_errors_in_light_and_dark()
    {
        var folder = Environment.GetEnvironmentVariable("APEX_SCREENSHOTS");
        var errors = OnStaThread(() => RenderAll(folder));
        // Whole, since the collection's own message cuts each one short.
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
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
                ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
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
        if (scenario.Narrow)
        {
            window.Width = window.MinWidth;
        }
        window.Show(main);
        try
        {
            ExpandAll(window, true);
            Settle(folder is null ? BindingsMs : AnimationsMs);
            // The cards with live readings only read the keyboard while open, so their expanders must drive it both ways.
            Assert.True(main.Calibration.IsOpen && main.Profile.IsOpen, $"{scenario.Name}: expanding did not open the cards");
            if (folder is not null)
            {
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
            ExpandAll(window, false);
            Settle(BindingsMs);
            Assert.False(main.Calibration.IsOpen || main.Profile.IsOpen, $"{scenario.Name}: collapsing did not close the cards");
        }
        finally
        {
            window.Close();
        }
    }

    private static void ExpandAll(Window window, bool expanded)
    {
        foreach (var expander in Descendants<CardExpander>(window))
        {
            expander.IsExpanded = expanded;
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
