using System.Windows;
using System.Windows.Controls;
using ApexMapper.App.ViewModels;
using ApexMapper.App.ViewModels.Profiles;

namespace ApexMapper.App.Views.Profiles;

public partial class ProfileSelectorView : UserControl
{
    public ProfileSelectorView() => InitializeComponent();

    private void EditBindings(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfileSelectorViewModel { SelectedProfile: { } profile } viewModel) return;
        var owner = Window.GetWindow(this);
        using var editing = (owner?.DataContext as MainWindowViewModel)?.BeginInputEditing();
        new BindingEditorWindow(profile, viewModel.SaveBindings) { Owner = owner }.ShowDialog();
    }
}
