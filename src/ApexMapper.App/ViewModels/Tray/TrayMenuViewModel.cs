using ApexMapper.App.Services;
using ApexMapper.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Tray;

public sealed class TrayMenuViewModel : ObservableViewModel
{
    private readonly ITrayService _trayService;
    private readonly ITrayProfileSource _profileSource;
    private readonly ISupervisorChannel _supervisorChannel;

    private bool _isEnabled;
    private IReadOnlyList<TrayProfileEntry> _profiles;
    private string _currentProfileName;

    public TrayMenuViewModel(
        ITrayService trayService,
        ITrayProfileSource profileSource,
        ISupervisorChannel supervisorChannel)
    {
        _trayService = trayService;
        _profileSource = profileSource;
        _supervisorChannel = supervisorChannel;

        _profiles = _profileSource.ListProfiles();
        _currentProfileName = ResolveCurrentProfileName();

        _profileSource.ProfilesChanged += OnProfilesChanged;

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
        IsEnabled = !IsEnabled;
        _trayService.SetEnabled(IsEnabled);
    }

    private void ExecuteSwitchProfile(string? profileId)
    {
        if (profileId is null) return;
        _profileSource.Switch(profileId);
        CurrentProfileName = ResolveCurrentProfileName();
    }

    private void ExecutePanic()
    {
        _ = _supervisorChannel.SubmitPanicAsync(CancellationToken.None);
    }

    private void ExecuteOpenMainWindow()
    {
        // TrayService owns event routing; no action needed here until TaskbarIcon wiring
    }

    private void ExecuteExit()
    {
        // ITrayService has no RequestExit() method; use the internal forwarder interface
        // so TrayService can raise its ExitRequested event on behalf of this command.
        (_trayService as ITrayServiceInternal)?.RequestExit();
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
}

