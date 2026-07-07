using ApexMapper.App.Services;
using ApexMapper.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Tray;

public sealed class TrayMenuViewModel : ObservableViewModel
{
    private readonly ITrayServiceInternal _trayService;
    private readonly ITrayProfileSource _profileSource;
    private readonly PanicCoordinator _panicCoordinator;
    private readonly IMappingSession _session;
    private readonly SynchronizationContext? _syncContext;

    private bool _isEnabled;
    private IReadOnlyList<TrayProfileEntry> _profiles;
    private string _currentProfileName;

    // Internal because ITrayServiceInternal is deliberately non-public; the
    // composition root constructs this type in-assembly and the test project
    // reaches it via InternalsVisibleTo.
    internal TrayMenuViewModel(
        ITrayServiceInternal trayService,
        ITrayProfileSource profileSource,
        PanicCoordinator panicCoordinator,
        IMappingSession session)
    {
        _trayService = trayService;
        _profileSource = profileSource;
        _panicCoordinator = panicCoordinator;
        _session = session;

        // Captured on the construction (UI) thread so session transitions —
        // which fire on whatever thread performed them — marshal back before
        // touching the tray icon (a WPF object). Null in unit tests: inline.
        _syncContext = SynchronizationContext.Current;

        _profiles = _profileSource.ListProfiles();
        _currentProfileName = ResolveCurrentProfileName();
        _isEnabled = _session.IsEnabled;

        _profileSource.ProfilesChanged += OnProfilesChanged;
        _session.StateChanged += OnSessionStateChanged;

        ToggleEnabledCommand = new RelayCommand(ExecuteToggleEnabled);
        SwitchProfileCommand = new RelayCommand<string>(ExecuteSwitchProfile);
        PanicCommand = new RelayCommand(ExecutePanic);
        OpenMainWindowCommand = new RelayCommand(ExecuteOpenMainWindow);
        ExitCommand = new RelayCommand(ExecuteExit);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    public IReadOnlyList<TrayProfileEntry> Profiles
    {
        get => _profiles;
        private set => SetProperty(ref _profiles, value);
    }

    public string CurrentProfileName
    {
        get => _currentProfileName;
        private set => SetProperty(ref _currentProfileName, value);
    }

    public IRelayCommand ToggleEnabledCommand { get; }
    public IRelayCommand<string> SwitchProfileCommand { get; }
    public IRelayCommand PanicCommand { get; }
    public IRelayCommand OpenMainWindowCommand { get; }
    public IRelayCommand ExitCommand { get; }

    private void ExecuteToggleEnabled()
    {
        // The session owns the state: the toggle only requests a transition and
        // the StateChanged event updates the menu, so a blocked enable (failed
        // pre-flight, declined confirmation, missing supervisor) leaves the
        // check-mark honest instead of optimistically flipped. Runs off the UI
        // thread: the enable flow probes drivers and may show a confirmation.
        // Both session methods are contract-bound never to throw.
        var enable = !IsEnabled;
        _ = Task.Run(() => enable
            ? _session.EnableAsync(CancellationToken.None)
            : _session.DisableAsync(CancellationToken.None));
    }

    private void ExecuteSwitchProfile(string? profileId)
    {
        if (profileId is null) return;
        _profileSource.Switch(profileId);
        CurrentProfileName = ResolveCurrentProfileName();
    }

    private void ExecutePanic()
    {
        _ = _panicCoordinator.PanicAsync(CancellationToken.None);
    }

    private void ExecuteOpenMainWindow()
    {
        _trayService.RequestOpenMainWindow();
    }

    private void ExecuteExit()
    {
        _trayService.RequestExit();
    }

    private string ResolveCurrentProfileName()
    {
        var currentId = _profileSource.CurrentProfileId;
        var profile = _profileSource.ListProfiles().FirstOrDefault(p => p.ProfileId == currentId);
        return profile?.DisplayName ?? currentId;
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        Profiles = _profileSource.ListProfiles();
        CurrentProfileName = ResolveCurrentProfileName();
    }

    private void OnSessionStateChanged(object? sender, MappingSessionStateChangedEventArgs e)
    {
        if (_syncContext is null)
        {
            ApplySessionState(e);
        }
        else
        {
            _syncContext.Post(state => ApplySessionState((MappingSessionStateChangedEventArgs)state!), e);
        }
    }

    private void ApplySessionState(MappingSessionStateChangedEventArgs e)
    {
        IsEnabled = e.IsEnabled;
        _trayService.SetEnabled(e.IsEnabled);
        if (!string.IsNullOrEmpty(e.Message))
        {
            _trayService.ShowBalloon("Apex Analog Mapper", e.Message);
        }
    }
}

