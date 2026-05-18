using System.Collections.Specialized;
using System.ComponentModel;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.App.ViewModels.Tray;

namespace ApexMapper.App.Services;

/// <summary>
/// Bridges ProfileSelectorViewModel to ITrayProfileSource.
/// Raises ProfilesChanged when the VM's Profiles collection changes or PinnedProfileId changes.
/// Re-subscribes to CollectionChanged whenever the Profiles reference is replaced.
/// </summary>
public sealed class TrayProfileSourceAdapter : ITrayProfileSource
{
    private readonly ProfileSelectorViewModel _vm;

    public event EventHandler? ProfilesChanged;

    public TrayProfileSourceAdapter(ProfileSelectorViewModel vm)
    {
        _vm = vm;
        _vm.Profiles.CollectionChanged += OnCollectionChanged;
        ((INotifyPropertyChanged)_vm).PropertyChanged += OnVmPropertyChanged;
    }

    // CurrentProfileId: returns pinned if set, else resolved, else empty string per interface contract.
    public string CurrentProfileId => _vm.CurrentProfileId ?? string.Empty;

    public IReadOnlyList<TrayProfileEntry> ListProfiles()
        => _vm.Profiles
            .Select(p => new TrayProfileEntry(p.Id, p.DisplayName))
            .ToList();

    public void Switch(string profileId) => _vm.PinCommand.Execute(profileId);

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ProfilesChanged?.Invoke(this, EventArgs.Empty);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileSelectorViewModel.Profiles))
        {
            // Re-subscribe to the new collection instance
            if (sender is ProfileSelectorViewModel vm)
                vm.Profiles.CollectionChanged += OnCollectionChanged;

            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(ProfileSelectorViewModel.PinnedProfileId))
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
