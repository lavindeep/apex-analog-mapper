using System.Collections.ObjectModel;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Profiles;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Profiles;

public sealed class ProfileSelectorViewModel : ObservableViewModel
{
    private readonly ProfileStore _store;
    private readonly IProfileManualPinStore _pinStore;
    private readonly Func<string?> _resolveCurrentId;

    private ObservableCollection<ProfileListItem> _profiles = [];
    private ProfileListItem? _selected;
    private string? _pinnedProfileId;
    private string? _resolvedProfileName;
    private IReadOnlyList<Profile> _loadedProfiles = [];
    private IReadOnlyList<BindingSummaryItem> _bindingSummary = [];
    private bool _loading;

    public ProfileSelectorViewModel(
        ProfileStore store,
        IProfileManualPinStore pinStore,
        Func<string?> resolveCurrentId)
    {
        _store = store;
        _pinStore = pinStore;
        _resolveCurrentId = resolveCurrentId;

        RefreshCommand = new RelayCommand(ExecuteRefresh);
        PinCommand = new RelayCommand<string>(ExecutePin);
        UnpinCommand = new RelayCommand(ExecuteUnpin);

        LoadFromStore();
    }

    public ObservableCollection<ProfileListItem> Profiles
    {
        get => _profiles;
        private set => SetProperty(ref _profiles, value);
    }

    public ProfileListItem? Selected
    {
        get => _selected;
        set
        {
            if (_loading || value is null || !SetProperty(ref _selected, value)) return;
            ExecutePin(value.Id);
        }
    }

    public IReadOnlyList<BindingSummaryItem> BindingSummary
    {
        get => _bindingSummary;
        private set => SetProperty(ref _bindingSummary, value);
    }

    public string? PinnedProfileId
    {
        get => _pinnedProfileId;
        private set => SetProperty(ref _pinnedProfileId, value);
    }

    public string? ResolvedProfileName
    {
        get => _resolvedProfileName;
        private set => SetProperty(ref _resolvedProfileName, value);
    }

    // CurrentProfileId: pinned takes priority over resolved
    public string? CurrentProfileId => PinnedProfileId ?? _resolveCurrentId();

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand<string> PinCommand { get; }
    public IRelayCommand UnpinCommand { get; }

    private void ExecuteRefresh() => LoadFromStore();

    private void ExecutePin(string? profileId)
    {
        if (profileId is null) return;
        _pinStore.Set(profileId);
        PinnedProfileId = profileId;
        UpdatePinnedFlags();
        UpdateSelection();
        OnPropertyChanged(nameof(CurrentProfileId));
    }

    private void ExecuteUnpin()
    {
        _pinStore.Set(null);
        PinnedProfileId = null;
        UpdatePinnedFlags();
        UpdateSelection();
        OnPropertyChanged(nameof(CurrentProfileId));
    }

    private void LoadFromStore()
    {
        _loading = true;
        try
        {
            var profiles = _store.LoadAll();
            _loadedProfiles = profiles;
            var pinnedId = _pinStore.Get();
            PinnedProfileId = pinnedId;

            var resolvedId = _resolveCurrentId();
            ResolvedProfileName = profiles.FirstOrDefault(p => p.Id == resolvedId)?.Name;

            var items = profiles
                .OrderBy(p => p.Name)
                .Select(p => new ProfileListItem
                {
                    Id = p.Id,
                    DisplayName = p.Name,
                    IsResolved = p.Id == resolvedId,
                    IsPinned = p.Id == pinnedId,
                })
                .ToList();

            var collection = new ObservableCollection<ProfileListItem>(items);
            // Replace the collection so Profiles property fires changed
            _profiles = collection;
            OnPropertyChanged(nameof(Profiles));
            UpdateSelection();
            OnPropertyChanged(nameof(CurrentProfileId));
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateSelection()
    {
        var currentId = CurrentProfileId;
        SetProperty(ref _selected, _profiles.FirstOrDefault(p => p.Id == currentId), nameof(Selected));
        var profile = _loadedProfiles.FirstOrDefault(p => p.Id == currentId);
        BindingSummary = profile is null ? [] : BindingSummaryItem.FromProfile(profile);
    }

    private void UpdatePinnedFlags()
    {
        var pinnedId = PinnedProfileId;
        foreach (var item in _profiles)
            item.IsPinned = item.Id == pinnedId;
    }
}
