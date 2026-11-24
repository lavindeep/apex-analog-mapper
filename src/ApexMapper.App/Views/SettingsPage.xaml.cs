using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ApexMapper.App.Views;

/// <summary>The controller driver, updates, the data folder, and two notes on starting the game.</summary>
public partial class SettingsPage : UserControl
{
    private bool _focusLeftUpdate;

    public SettingsPage() => InitializeComponent();

    // Update hides while it checks or downloads, and the driver button once the driver is
    // installed. Either can hold keyboard focus as it hides, so focus moves to a button that
    // only opens something, never to a switch a second Space would flip. Focus that left
    // Update goes back to it when it returns, unless the user moved it since.
    private void OnButtonVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var shown = e.NewValue is true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (shown)
            {
                if (sender == UpdateButton && _focusLeftUpdate && FolderButton.IsKeyboardFocused)
                {
                    UpdateButton.Focus();
                }
                _focusLeftUpdate &= sender != UpdateButton;
                return;
            }
            if (IsVisible && Keyboard.FocusedElement is not UIElement { IsVisible: true })
            {
                UIElement next = UpdateButton is { IsVisible: true, IsEnabled: true } ? UpdateButton
                    : ReleasesButton.IsVisible ? ReleasesButton : FolderButton;
                next.Focus();
                _focusLeftUpdate = sender == UpdateButton;
            }
        });
    }
}
