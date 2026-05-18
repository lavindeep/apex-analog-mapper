using ApexMapper.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApexMapper.App.ViewModels.Profiles;

public sealed partial class ProfileListItem : ObservableViewModel
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _isResolved;

    [ObservableProperty]
    private bool _isPinned;
}
