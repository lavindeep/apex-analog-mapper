using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.App.ViewModels.Tray;

namespace ApexMapper.App.Services;

/// <summary>
/// Bridges ProfileSelectorViewModel to ITrayProfileSource.
/// Raises ProfilesChanged when the VM's Profiles collection changes or PinnedProfileId changes.
/// Re-subscribes to CollectionChanged whenever the Profiles reference is replaced.
/// Implements IDisposable to cleanly unsubscribe from all events.
/// </summary>
public sealed class TrayProfileSourceAdapter : ITrayProfileSource, IDisposable
{
    private readonly ProfileSelectorViewModel _vm;
    private ObservableCollection<ProfileListItem>? _subscribedCollection;

    public event EventHandler? ProfilesChanged;

    public TrayProfileSourceAdapter(ProfileSelectorViewModel vm)
    {
        _vm = vm;
        _subscribedCollection = _vm.Profiles;
        _subscribedCollection.CollectionChanged += OnCollectionChanged;
        ((INotifyPropertyChanged)_vm).PropertyChanged += OnVmPropertyChanged;
    }

    // CurrentProfileId: returns pinned if set, else resolved, else empty string per interface contract.
    public string CurrentProfileId => _vm.CurrentProfileId ?? string.Empty;

    public IReadOnlyList<TrayProfileEntry> ListProfiles()
        => _vm.Profiles
            .Select(p => new TrayProfileEntry(p.Id, p.DisplayName))
            .ToList();

    public void Switch(string profileId) => _vm.PinCommand.Execute(profileId);

    public void Dispose()
    {
        ((INotifyPropertyChanged)_vm).PropertyChanged -= OnVmPropertyChanged;
        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnCollectionChanged;
            _subscribedCollection = null;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ProfilesChanged?.Invoke(this, EventArgs.Empty);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileSelectorViewModel.Profiles))
        {
            // Unsubscribe from old collection, subscribe to new one
            if (_subscribedCollection is not null)
                _subscribedCollection.CollectionChanged -= OnCollectionChanged;

            _subscribedCollection = _vm.Profiles;
            _subscribedCollection.CollectionChanged += OnCollectionChanged;

            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(ProfileSelectorViewModel.PinnedProfileId))
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
