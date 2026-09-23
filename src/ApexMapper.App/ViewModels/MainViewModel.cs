using ApexMapper.App.Model;
using ApexMapper.App.Storage;

namespace ApexMapper.App.ViewModels;

/// <summary>
/// The six cards, top to bottom, sharing one <see cref="Model.Workspace"/>. The window's
/// timer calls <see cref="Tick"/>: fast while something shows live readings or waits for
/// a key, slow otherwise, so a session in the background costs the UI thread little.
/// Each tick drains Raw Input's key events for key capture and the other-keyboard notice.
/// </summary>
public sealed class MainViewModel
{
    public const int FastTickMs = 33;
    public const int SlowTickMs = StatusViewModel.RefreshMs;

    private readonly AppServices _services;
    private long? _lastStatus;

    public MainViewModel(AppServices services, Workspace workspace, AppSettings settings)
    {
        _services = services;
        Workspace = workspace;
        Keyboard = new KeyboardViewModel(services, workspace, settings.Keyboard, settings.ConsentedKeyboards);
        Game = new GameViewModel(services, workspace, settings.GamePath);
        Profile = new ProfileViewModel(services, workspace, settings.ActiveProfile);
        Calibration = new CalibrationViewModel(services, workspace);
        Status = new StatusViewModel(services, workspace);
        Setup = new SetupViewModel(services, workspace);
        Status.CalibrationRequested += () =>
        {
            Calibration.IsOpen = true;
            CardRequested?.Invoke(Calibration);
        };
        Keyboard.OnKeyboards(services.Keyboards.Current);
        Game.Rescan();
    }

    /// <summary>The window scrolls this card into view.</summary>
    public event Action<object>? CardRequested;

    public Workspace Workspace { get; }

    public StatusViewModel Status { get; }

    public KeyboardViewModel Keyboard { get; }

    public GameViewModel Game { get; }

    public ProfileViewModel Profile { get; }

    public CalibrationViewModel Calibration { get; }

    public SetupViewModel Setup { get; }

    /// <summary>The window came back to the front: the driver may be installed now, and the game may be running.</summary>
    public void OnActivated()
    {
        Setup.Recheck();
        Game.OnActivated();
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
