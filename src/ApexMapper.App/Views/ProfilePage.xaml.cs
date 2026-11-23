using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ApexMapper.App.ViewModels;

namespace ApexMapper.App.Views;

/// <summary>
/// The profiles and their bindings, one open at a time, with key capture and the response
/// preview. Opening a binding can close a tall one above it, an added binding opens at
/// the end of the list, and a capture's prompt opens under the header, so the open
/// binding's header and first controls scroll into view once laid out.
/// </summary>
public partial class ProfilePage : UserControl
{
    // The header and the first controls under it: keys, controller input and preset.
    private const double OpenedHeight = 320;

    public ProfilePage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ProfileViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
                old.Rows.CollectionChanged -= OnRowsChanged;
            }
            if (e.NewValue is ProfileViewModel now)
            {
                now.PropertyChanged += OnViewModelChanged;
                now.Rows.CollectionChanged += OnRowsChanged;
            }
        };
    }

    /// <summary>Delete hides once the Forza profile is back.</summary>
    private void OnDeleteHidden(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            KeepFocus();
        }
    }

    /// <summary>Removing a binding takes its own button with it.</summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove)
        {
            KeepFocus();
        }
    }

    /// <summary>
    /// A focused button that turns off or goes away leaves the keyboard nowhere: a capture
    /// turns the editor off, a save waits for mapping to stop, and Delete and Remove take
    /// their own buttons. Once laid out, focus goes to the open binding's first button, or
    /// to the first add button that is on, or to the profile list.
    /// </summary>
    private void KeepFocus() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (!IsVisible || Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true })
        {
            return;
        }
        var open = DataContext is ProfileViewModel { SelectedRow: { } row } && Bindings.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
            ? VisualTree.Descendants(container).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.IsEnabled)
            : null;
        _ = open?.Focus() == true || AddKeyButton.Focus() || AddAxisButton.Focus() || ProfilePicker.Focus();
    });

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileViewModel.IsCapturing) or nameof(ProfileViewModel.IsDirty) && sender is ProfileViewModel { IsCapturing: false })
        {
            KeepFocus();
        }
        if (e.PropertyName == nameof(ProfileViewModel.IsAdding) && sender is ProfileViewModel { IsAdding: true } && IsVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AddPrompt.BringIntoView());
            return;
        }
        if (e.PropertyName is not (nameof(ProfileViewModel.SelectedRow) or nameof(ProfileViewModel.IsCapturing))
            || sender is not ProfileViewModel { SelectedRow: { } row } || !IsVisible)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (row.IsExpanded && Bindings.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                container.BringIntoView(new Rect(0, 0, container.ActualWidth, Math.Min(OpenedHeight, container.ActualHeight)));
            }
        });
    }
}
