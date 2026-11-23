using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.App.Storage;

namespace ApexMapper.App.ViewModels;

/// <summary>The window's pages. Home holds what a driver needs each time; the others hold the finer settings.</summary>
public enum Page
{
    Home,
    Keyboard,
    Profile,
    Calibration,
    Settings,
}

/// <summary>
/// The window's view models, sharing one <see cref="Model.Workspace"/>, and which page
/// shows. The window's timer calls <see cref="Tick"/>: fast while something shows live
/// readings or waits for a key, slow otherwise, so a session in the background costs the
/// UI thread little. Each tick drains Raw Input's key events for key capture and the
/// other-keyboard notice.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    public const int FastTickMs = 33;
    public const int SlowTickMs = StatusViewModel.RefreshMs;

    private readonly AppServices _services;
    private long? _lastStatus;
    private Page _page;

    public MainViewModel(AppServices services, Workspace workspace, AppSettings settings)
    {
        _services = services;
        Workspace = workspace;
        Keyboard = new KeyboardViewModel(services, workspace, settings.Keyboard, settings.ConsentedKeyboards);
        Game = new GameViewModel(services, workspace, settings.GamePath);
        Profile = new ProfileViewModel(services, workspace, settings.ActiveProfile);
        Calibration = new CalibrationViewModel(services, workspace);
        Status = new StatusViewModel(services, workspace);
        Setup = new SetupViewModel(services, workspace, settings);
        Status.CalibrationRequested += () => Page = Page.Calibration;
        Status.KeyboardRequested += () => Page = Page.Keyboard;
        OpenKeyboard = new Command(() => Page = Page.Keyboard);
        OpenProfile = new Command(() => Page = Page.Profile);
        OpenCalibration = new Command(() => Page = Page.Calibration);
        OpenSettings = new Command(() => Page = Page.Settings);
        GoHome = new Command(() => Page = Page.Home);
        Keyboard.OnKeyboards(services.Keyboards.Current);
        Game.Rescan();
    }

    /// <summary>
    /// The page on show. The profile and calibration pages read the keyboard only while
    /// they show, leaving the profile page ends a key capture, and leaving the keyboard
    /// page ends a sensor check, whose prompt shows only there.
    /// </summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value))
            {
                return;
            }
            if (value != Page.Profile)
            {
                Profile.CancelCapture.Execute(null);
            }
            if (value != Page.Keyboard)
            {
                Keyboard.StopCheck("The check stopped when you left the Keyboard page. Run it again.");
            }
            Profile.IsOpen = value == Page.Profile;
            Calibration.IsOpen = value == Page.Calibration;
            Raise(nameof(PageTitle));
        }
    }

    public string PageTitle => _page switch
    {
        Page.Keyboard => "Keyboard",
        Page.Profile => "Profile",
        Page.Calibration => "Calibration",
        Page.Settings => "Settings",
        _ => "Home",
    };

    public Command OpenKeyboard { get; }

    public Command OpenProfile { get; }

    public Command OpenCalibration { get; }

    public Command OpenSettings { get; }

    public Command GoHome { get; }

    public Workspace Workspace { get; }

    public StatusViewModel Status { get; }

    public KeyboardViewModel Keyboard { get; }

    public GameViewModel Game { get; }

    public ProfileViewModel Profile { get; }

    public CalibrationViewModel Calibration { get; }

    public SetupViewModel Setup { get; }

    /// <summary>
    /// The window came back to the front: the driver may be installed now, and the game may be running.
    /// Raw Input sends nothing while the lock screen or a UAC prompt is in front, so a key released
    /// then never sent its key-up; the cards forget which keys are held.
    /// </summary>
    public void OnActivated()
    {
        Setup.Recheck();
        Game.OnActivated();
        Profile.ForgetHeldKeys();
        Calibration.ForgetHeldKeys();
    }

    /// <summary>Another window came to the front: what it types belongs to it, so a key capture and a sensor check end.</summary>
    public void OnDeactivated()
    {
        Profile.CancelCapture.Execute(null);
        Keyboard.StopCheck("The check stopped when you switched to another window. Run it again.");
    }

    public int TickIntervalMs => Calibration.IsOpen || Profile.IsOpen || Profile.IsCapturing || Keyboard.IsChecking ? FastTickMs : SlowTickMs;

    public void Tick()
    {
        var now = _services.NowMs();
        while (_services.KeyEvents.TryRead(out var key))
        {
            Profile.OnKey(key);
            Calibration.OnKey(key);
            Status.OnKey(key);
        }
        Keyboard.Tick(now);
        Calibration.Tick(now);
        Profile.Tick();
        Game.Tick(now);
        if (_lastStatus is not { } last || now - last >= StatusViewModel.RefreshMs)
        {
            _lastStatus = now;
            Status.Tick(now);
        }
    }
}
