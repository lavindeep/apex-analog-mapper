using System.IO;
using System.Windows;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.Core.Engine;

namespace ApexMapper.App.Views.Profiles;

public partial class BindingEditorWindow : Window
{
    private readonly BindingEditorViewModel _editor;
    private readonly Action<Profile> _save;

    public BindingEditorWindow(Profile profile, Action<Profile> save)
    {
        _editor = new BindingEditorViewModel(profile);
        _save = save;
        InitializeComponent();
        DataContext = _editor;
    }

    private void SaveBindings(object sender, RoutedEventArgs e)
    {
        if (!_editor.TryBuildProfile(out var profile)) return;
        try
        {
            _save(profile!);
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _editor.Error = $"Could not save bindings. {error.Message}";
        }
    }
}
