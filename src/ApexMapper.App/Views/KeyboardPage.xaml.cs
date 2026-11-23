using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ApexMapper.App.ViewModels;

namespace ApexMapper.App.Views;

/// <summary>
/// The plugged-in boards, the firmware, and the try-it steps for an untested model. A
/// check started from step 1 runs in step 2, which scrolls into view so its prompt to
/// hold W is on screen when it comes.
/// </summary>
public partial class KeyboardPage : UserControl
{
    public KeyboardPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is KeyboardViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }
            if (e.NewValue is KeyboardViewModel now)
            {
                now.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyboardViewModel.IsChecking) && sender is KeyboardViewModel { IsChecking: true } && IsVisible)
        {
            // Once laid out: step 1 collapses and step 2's prompt appears in the same change.
            // Consent hides the button that had focus, so focus follows the check.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                CheckStep.BringIntoView();
                if (Keyboard.FocusedElement is not UIElement { IsVisible: true })
                {
                    CheckStep.Focus();
                }
            });
        }
    }
}
