using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApexMapper.App.Storage;
using ApexMapper.App.Tests.ViewModels;
using ApexMapper.App.ViewModels;
using ApexMapper.App.Views;
using ApexMapper.Core.Bindings;
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
/// Shows the real window offscreen, over fake services, on every page in a few states and
/// in light and dark, and fails on any binding WPF could not resolve. Nothing reaches a
/// keyboard or the driver. Set APEX_SCREENSHOTS to a folder to keep an image of every page.
/// </summary>
public sealed class WindowRenderingTests
{
    private const double Scale = 1.5;

    /// <summary>Long enough for WPF-UI's expand and open animations to finish, which only images need.</summary>
    private const int AnimationsMs = 800;

    /// <summary>Long enough for layout and every binding to resolve.</summary>
    private const int BindingsMs = 50;

    /// <param name="Act">Runs before the window shows. It may leave the window on a page other than Home.</param>
    /// <param name="Narrow">Shown at the window's minimum width.</param>
    /// <param name="Wide">Shown as wide as a maximized window on a 1600-pixel screen.</param>
    /// <param name="Version">The app's version, when it is not the harness's.</param>
    /// <param name="Game">The game saved from an earlier run, or null for none.</param>
    private sealed record Scenario(string Name, Action<AppHarness> Arrange, Func<MainViewModel, AppHarness, Task>? Act = null, bool Narrow = false, bool Wide = false, string? Version = null,
        string? Game = AppHarness.Game);

    private const string SteamGame = @"C:\Program Files (x86)\Steam\steamapps\common\ForzaHorizon6\ForzaHorizon6.exe";

    /// <summary>W calibrated and the rest not, with Forza found.</summary>
    private static void Calibrating(AppHarness h)
    {
        h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
        h.Services.Calibrations.Put(AppHarness.Tkl, AppHarness.Firmware, DefaultProfiles.Key.W, KeyCalibration.Create(850, 3900, 40, 16));
    }

    /// <summary>Ticks the window's timer for <paramref name="ms"/> of the harness clock.</summary>
    private static void Advance(MainViewModel main, AppHarness h, int ms)
    {
        var end = h.Now + ms;
        for (; h.Now <= end; h.Now += 50)
        {
            main.Tick();
        }
    }

    private static void Ready(AppHarness h)
    {
        h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
        h.CalibrateForza();
    }

    private static readonly Scenario[] Scenarios =
    [
        new("first-run", h => h.Boards.Clear(), Game: null),
        new("two-keyboards", h => h.Boards.Add(AppHarness.Gen3Info)),
        new("no-game", h => h.CalibrateForza(), Game: null),
        new("ready", Ready),
        new("ready-narrow", Ready, Narrow: true),
        new("ready-wide", Ready, Wide: true),
        new("steam-game", h =>
        {
            h.Windows = [new GameWindow("Forza Horizon 6", SteamGame, Elevation.Visible)];
            h.CalibrateForza();
        }, Game: SteamGame),
        new("calibrating", h =>
        {
            h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
            h.Services.Calibrations.Put(AppHarness.Tkl, AppHarness.Firmware, DefaultProfiles.Key.W, KeyCalibration.Create(850, 4095, 40, 16));
            h.Sensor.Reading = AppHarness.Holding(16, 2400);
        }, (main, _) =>
        {
            main.Page = Page.Calibration;
            main.Tick();
            return Task.CompletedTask;
        }),
        new("calibration-open", Ready, (main, h) =>
        {
            main.Page = Page.Calibration;
            main.Calibration.Rows.First(r => r.Key == DefaultProfiles.Key.W).IsExpanded = true;
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Tick();
            return Task.CompletedTask;
        }),
        // The poller gave up with a reason while the page reads the keyboard, with W open.
        new("calibration-sensor-problem", Ready, (main, h) =>
        {
            main.Page = Page.Calibration;
            main.Calibration.Rows.First(r => r.Key == DefaultProfiles.Key.W).IsExpanded = true;
            h.Sensor.Reading = null;
            h.Sensor.Problem = "Reply padding was not zero.";
            main.Tick();
            return Task.CompletedTask;
        }),
        // The keyboard's firmware changed since W was calibrated, and W is open.
        new("calibration-other-firmware", h =>
        {
            h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
            h.Services.Calibrations.Put(AppHarness.Tkl, "4.9.1", DefaultProfiles.Key.W, KeyCalibration.Create(850, 3900, 40, 16));
        }, (main, h) =>
        {
            main.Page = Page.Calibration;
            main.Calibration.Rows.First(r => r.Key == DefaultProfiles.Key.W).IsExpanded = true;
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Tick();
            return Task.CompletedTask;
        }),
        // S's released reading taken, so step 2 is the one to do.
        new("calibration-released", Calibrating, (main, h) =>
        {
            main.Page = Page.Calibration;
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Tick();
            main.Calibration.Rows.First(r => r.Key == DefaultProfiles.Key.S).SetReleased.Execute(null);
            Advance(main, h, CalibrationViewModel.ReleasedMs);
            return Task.CompletedTask;
        }, Narrow: true),
        // S saved, and A named as the key to do next.
        new("calibration-saved", Calibrating, (main, h) =>
        {
            main.Page = Page.Calibration;
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Tick();
            var s = main.Calibration.Rows.First(r => r.Key == DefaultProfiles.Key.S);
            s.SetReleased.Execute(null);
            Advance(main, h, CalibrationViewModel.ReleasedMs);
            h.Sensor.Reading = AppHarness.Holding(s.SensorIndex!.Value, 3900);
            s.SetPressed.Execute(null);
            Advance(main, h, CalibrationViewModel.PressedMs);
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Tick();
            return Task.CompletedTask;
        }, Narrow: true),
        new("calibration-step", Ready, (main, h) =>
        {
            main.Page = Page.Calibration;
            var w = main.Calibration.Rows.First(r => r.Key == DefaultProfiles.Key.W);
            w.IsExpanded = true;
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Tick();
            w.SetReleased.Execute(null);
            main.Tick();
            return Task.CompletedTask;
        }, Narrow: true),
        new("mapping", Ready, async (main, h) =>
        {
            await main.Status.StartAsync();
            // The game has not started yet, as the session says below.
            h.Windows = [];
            main.Game.Rescan();
            var status = new SessionStatus(SessionState.Running, null, GameRunning: false, GameHasFocus: false, GameElevation: Elevation.Visible, FallbackKeys: 1,
                SensorProblem: "The keyboard stopped answering.", KeysAwaitingRelease: false, SubmitCount: 0, HookReinstalls: 1, RestartRequired: false,
                CycleP50Ms: 1.5f, CycleP99Ms: 2.9f, KeysAtLimit: [DefaultProfiles.Key.S]);
            for (var ms = 0; ms <= StatusViewModel.AtLimitWarningMs; ms += 500)
            {
                h.Session.NextStatus = status with { SubmitCount = ms / 2 };
                main.Status.Tick(h.Now + ms);
            }
        }),
        new("paused", Ready, async (main, h) =>
        {
            await main.Status.StartAsync();
            // The keyboard was unplugged, which is what pauses a session.
            h.Session.Move(SessionState.Paused);
            h.Boards.Clear();
            main.Keyboard.OnKeyboards(h.Boards);
            main.Status.Tick(h.Now);
        }),
        new("mapping-background-narrow", Ready, async (main, h) =>
        {
            await main.Status.StartAsync();
            h.Session.NextStatus = new SessionStatus(SessionState.Running, null, GameRunning: true, GameHasFocus: false, GameElevation: Elevation.Visible, FallbackKeys: 0,
                SensorProblem: null, KeysAwaitingRelease: false, SubmitCount: 0, HookReinstalls: 0, RestartRequired: false, CycleP50Ms: 1.5f, CycleP99Ms: 2.9f);
            main.Status.Tick(h.Now);
        }, Narrow: true),
        new("untested-board", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }),
        new("untested-board-check-passed", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }, (main, h) =>
        {
            main.Page = Page.Keyboard;
            main.Keyboard.Consent.Execute(null);
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs);
            h.Sensor.Reading = AppHarness.Holding(16, 3000);
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs + 100);
            return Task.CompletedTask;
        }),
        new("untested-board-check-other-sensor", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }, (main, h) =>
        {
            main.Page = Page.Keyboard;
            main.Keyboard.Consent.Execute(null);
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs);
            h.Sensor.Reading = AppHarness.Holding(17, 3000);
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs + 100);
            return Task.CompletedTask;
        }, Narrow: true),
        new("untested-board-check-failed", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }, (main, h) =>
        {
            main.Page = Page.Keyboard;
            main.Keyboard.Consent.Execute(null);
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs);
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs + KeyboardViewModel.CheckTimeoutMs + 1);
            return Task.CompletedTask;
        }),
        new("untested-board-check-unreadable", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }, (main, h) =>
        {
            main.Page = Page.Keyboard;
            main.Keyboard.Consent.Execute(null);
            h.Sensor.Problem = "Reply padding was not zero.";
            main.Keyboard.Tick(h.Now + KeyboardViewModel.CheckTimeoutMs + 1);
            return Task.CompletedTask;
        }),
        new("untested-board-checking", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }, (main, h) =>
        {
            main.Page = Page.Keyboard;
            main.Keyboard.Consent.Execute(null);
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs);
            return Task.CompletedTask;
        }),
        new("untested-board-exported", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
            Directory.CreateDirectory(h.File("Downloads"));
            h.Dialogs.SavePath = h.File(@"Downloads\capture.json");
        }, (main, h) =>
        {
            main.Page = Page.Keyboard;
            main.Keyboard.Consent.Execute(null);
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs);
            h.Sensor.Reading = AppHarness.Holding(16, 3000);
            main.Keyboard.Tick(h.Now + CalibrationViewModel.SettleMs + 100);
            main.Keyboard.Export.Execute(null);
            return Task.CompletedTask;
        }, Narrow: true),
        new("untested-board-mapping", h =>
        {
            Ready(h);
            h.CalibrateForza(AppHarness.Gen3, "1.2.0");
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading("1.2.0", null, null);
        }, async (main, h) =>
        {
            // An untested board maps only after consent, which also starts a check that mapping then ends.
            main.Keyboard.Consent.Execute(null);
            await main.Status.StartAsync();
            main.Page = Page.Keyboard;
        }),
        new("untested-board-no-firmware", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info;
            h.FirmwareOf = _ => new FirmwareReading(null, null, "The device is busy.");
        }),
        new("untested-firmware", h => h.FirmwareOf = _ => new FirmwareReading("9.9.9", null, null)),
        new("no-sensor-interface", h =>
        {
            h.Boards[0] = AppHarness.TklInfo with { HasVendorInterface = false };
            h.FirmwareOf = _ => new FirmwareReading(null, null, "No vendor interface found for the selected keyboard.");
        }),
        new("unsupported-board", h =>
        {
            h.Boards[0] = AppHarness.Gen3Info with { ProductId = 0x1646, Name = "Apex Pro TKL Wireless Gen 3" };
            h.FirmwareOf = _ => new FirmwareReading("3.2.1", null, null);
        }),
        new("driver-missing", h => h.DriverCheck = () => DriverState.Missing),
        new("driver-not-started", h => h.DriverCheck = () => DriverState.NotStarted),
        new("driver-missing-narrow", h => h.DriverCheck = () => DriverState.Missing, Narrow: true),
        new("restart-required", h =>
        {
            h.CalibrateForza();
            h.Session.RestartRequired = true;
            h.Session.LastEnd = SessionEnd.For(EndReason.EngineStalled);
        }),
        new("axis-capture", _ => { }, (main, _) =>
        {
            main.Page = Page.Profile;
            main.Profile.SelectedRow = main.Profile.Rows.First(r => r.IsAxis);
            main.Profile.CaptureNegativeKey.Execute(null);
            return Task.CompletedTask;
        }, Narrow: true),
        new("custom-response", h => h.CalibrateForza(), (main, h) =>
        {
            main.Page = Page.Profile;
            var row = main.Profile.Rows.First(r => r.Key == DefaultProfiles.Key.W);
            main.Profile.SelectedRow = row;
            row.Preset = ResponsePreset.Custom;
            row.Deadzone = 0.1;
            h.Sensor.Reading = AppHarness.Holding(16, 2375);
            main.Profile.Tick();
            return Task.CompletedTask;
        }),
        new("custom-response-wide", h => h.CalibrateForza(), (main, h) =>
        {
            main.Page = Page.Profile;
            var row = main.Profile.Rows.First(r => r.Key == DefaultProfiles.Key.W);
            main.Profile.SelectedRow = row;
            row.Preset = ResponsePreset.Custom;
            row.Deadzone = 0.1;
            h.Sensor.Reading = AppHarness.Holding(16, 2375);
            main.Profile.Tick();
            return Task.CompletedTask;
        }, Wide: true),
        new("custom-stick-uncalibrated", _ => { }, (main, h) =>
        {
            main.Page = Page.Profile;
            var row = main.Profile.Rows.First(r => r.Target == PadTarget.LeftStickX);
            main.Profile.SelectedRow = row;
            row.Preset = ResponsePreset.Custom;
            h.Sensor.Reading = FakeSensor.AtRest();
            main.Profile.Tick();
            return Task.CompletedTask;
        }, Narrow: true),
        new("dead-zone-past-full", _ => { }, (main, _) =>
        {
            main.Page = Page.Profile;
            var row = main.Profile.Rows.First(r => r.Key == DefaultProfiles.Key.W);
            main.Profile.SelectedRow = row;
            row.Preset = ResponsePreset.Custom;
            row.Saturation = 0.4;
            row.Deadzone = 0.5;
            return Task.CompletedTask;
        }, Narrow: true),
        new("add-binding", _ => { }, (main, _) =>
        {
            main.Page = Page.Profile;
            main.Profile.AddAxis.Execute(null);
            return Task.CompletedTask;
        }, Narrow: true),
        new("new-profile", _ => { }, (main, _) =>
        {
            main.Page = Page.Profile;
            main.Profile.New.Execute(null);
            main.Profile.SelectedRow = main.Profile.Rows.First(r => !r.ShapesOutput);
            return Task.CompletedTask;
        }),
        new("profile-edited-while-mapping", Ready, async (main, _) =>
        {
            await main.Status.StartAsync();
            main.Page = Page.Profile;
            main.Profile.Rows.First(r => r.IsAxis).Preset = ResponsePreset.Custom;
            main.Profile.Rows.First(r => r.IsAxis).Mode = AxisMode.Rate;
            main.Profile.SelectedRow = main.Profile.Rows.First(r => r.IsAxis);
        }, Narrow: true),
        new("update-available", h =>
        {
            Ready(h);
            h.Updates.Newest = "0.6.0";
        }),
        new("ci-build", h => h.Updates.Newest = "0.5.1-alpha", Version: "0.1.0-ci.137"),
        new("not-installed", h => h.Updates.Installed = false),
    ];

    [Fact]
    public void Every_page_shows_with_no_binding_errors_in_light_and_dark()
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

    /// <summary>Shows the page the scenario left open, then every other page, ending on Home.</summary>
    private static void Render(Scenario scenario, ApplicationTheme theme, string? folder)
    {
        using var h = scenario.Version is { } version ? new AppHarness(version) : new AppHarness();
        scenario.Arrange(h);
        h.Keyboards.Refresh();
        var main = new MainViewModel(h.Services, h.Workspace, new AppSettings(GamePath: scenario.Game));
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
        else if (scenario.Wide)
        {
            window.Width = 1600;
        }
        window.Show(main);
        try
        {
            var first = main.Page;
            var pages = Enum.GetValues<Page>().Where(page => page != first && page != Page.Home).Prepend(first).Append(Page.Home).Distinct();
            foreach (var page in pages)
            {
                main.Page = page;
                Settle(folder is null ? BindingsMs : AnimationsMs);
                Check(window, main, $"{scenario.Name}, {page}", page);
                if (folder is not null)
                {
                    Directory.CreateDirectory(folder);
                    SaveWindow(window, Path.Combine(folder, $"{scenario.Name}-{theme.ToString().ToLowerInvariant()}-{(int)page}-{page}.png"));
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void Check(MainWindow window, MainViewModel main, string where, Page page)
    {
        // The pages with live readings read the keyboard only while they show.
        Assert.True(main.Calibration.IsOpen == (page == Page.Calibration) && main.Profile.IsOpen == (page == Page.Profile), $"{where}: the wrong page reads the keyboard");
        // The selected binding's buttons say which key each one changes, in text a screen reader reads too.
        if (page == Page.Profile && main.Profile.SelectedRow is { } row)
        {
            var labels = Descendants<Button>(window).Where(button => button.IsVisible).Select(button => button.Content).OfType<string>().ToList();
            Assert.Contains(row.ChangeKeyText, labels);
            Assert.True(!row.IsAxis || labels.Contains(row.ChangeNegativeKeyText), $"{where}: no button says {row.ChangeNegativeKeyText}");
            // The Default preset can't be edited, so the open binding's settings show only for Custom, and no other binding is open.
            var curves = Descendants<System.Windows.Controls.Slider>(window).Count(slider => AutomationProperties.GetName(slider) == "Curve" && slider.IsVisible);
            Assert.True(curves == (row.ShapesOutput && row.IsCustom ? 1 : 0), $"{where}: {curves} curve sliders show");
        }
        // A screen reader reads each live text by the name it gives its element: the state on Home, and the capture prompt while it shows.
        var live = Descendants<FrameworkElement>(window).Where(e => !string.IsNullOrEmpty(Live.GetText(e))).ToList();
        Assert.All(live, e => Assert.Equal((Live.GetText(e), AutomationLiveSetting.Polite), (AutomationProperties.GetName(e), AutomationProperties.GetLiveSetting(e))));
        Assert.All(live.Where(e => e.IsVisible), e => Assert.Equal((Live.GetText(e), AutomationLiveSetting.Polite),
            Live.PeerFor(e, Live.GetText(e)!) is { } peer ? (peer.GetName(), peer.GetLiveSetting()) : default));
        Assert.True(page != Page.Home || live.Any(e => e.IsVisible && Live.GetText(e) == main.Status.StateText), $"{where}: the state is not live");
        Assert.True(main.Profile.Prompt is null || live.Any(e => e.IsVisible && Live.GetText(e) == main.Profile.Prompt), $"{where}: the capture prompt is not live");
        // A button with no text of its own has a name for a screen reader.
        Assert.All(Descendants<Button>(window).Where(button => button.IsVisible && button.Content is not string), button =>
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)), $"{where}: a button has no name"));
        Assert.All(Descendants<TitleBarButton>(window).Where(button => button.IsVisible), button =>
            Assert.Equal(button.ButtonType.ToString(), AutomationProperties.GetName(button)));
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

    /// <summary>
    /// What sits above the scroll area (the title bar, and a page's header) over the whole
    /// scrolling column. Windows keeps a window no taller than the screen, so the column is
    /// drawn on its own rather than through the scroll viewer.
    /// </summary>
    private static void SaveWindow(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var scroller = Descendants<System.Windows.Controls.ScrollViewer>(window).First();
        var column = (FrameworkElement)scroller.Content;
        var top = scroller.TranslatePoint(default, root).Y;
        var margin = column.Margin;
        var left = column.TranslatePoint(default, root).X - margin.Left;
        // A visual brush draws an element where its parent placed it, so the view starts at the column's offset.
        var offset = VisualTreeHelper.GetOffset(column);
        // Desired, not actual: the scroll viewer stretches the column to fill it.
        var slot = new Rect(left, top, column.ActualWidth + margin.Left + margin.Right, column.DesiredSize.Height);
        var height = slot.Bottom;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var above = new Rect(0, 0, root.ActualWidth, top);
            context.DrawRectangle((Brush)Application.Current.Resources["ApplicationBackgroundBrush"], null, new Rect(0, 0, root.ActualWidth, height));
            context.DrawRectangle(new VisualBrush(root) { Viewbox = above, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None }, null, above);
            var view = new Rect(offset.X - margin.Left, offset.Y - margin.Top, slot.Width, slot.Height);
            context.DrawRectangle(new VisualBrush(column) { Viewbox = view, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None }, null, slot);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, path);
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
