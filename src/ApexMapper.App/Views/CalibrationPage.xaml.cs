using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ApexMapper.App.ViewModels;

namespace ApexMapper.App.Views;

/// <summary>
/// One row per analog key: the live reading, the saved marks, and the calibration steps.
/// A step's message tells the user when to press the key, so once a step starts its
/// message scrolls into view. The step hides the button that started it, so the
/// keyboard moves to the key's header while it runs, and after it to the button the
/// result asks for next, or to the next key's header once a key is saved. While a step runs the page ignores keys, so the key being
/// calibrated can't scroll the page, move focus or press a button; Raw Input still reads it.
/// A key it ignored stays ignored until it comes up, so one still held when the step ends
/// can't press the button that just took focus.
/// </summary>
public partial class CalibrationPage : UserControl
{
    private readonly HashSet<Key> _ignored = [];
    private CalibrationRowViewModel? _running;

    public CalibrationPage()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (DataContext is CalibrationViewModel { IsBusy: true } && e.Key != Key.System)
            {
                _ignored.Add(e.Key);
            }
            e.Handled |= _ignored.Contains(e.Key);
        };
        PreviewKeyUp += (_, e) => e.Handled |= _ignored.Remove(e.Key);
        IsVisibleChanged += (_, _) => _ignored.Clear();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is CalibrationViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }
            if (e.NewValue is CalibrationViewModel now)
            {
                now.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CalibrationViewModel.ActiveRow) || sender is not CalibrationViewModel calibration)
        {
            return;
        }
        var started = calibration.ActiveRow;
        var ended = started is null ? _running : null;
        _running = started;
        if (!IsVisible)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (started is not null && Keys.ItemContainerGenerator.ContainerFromItem(started) is DependencyObject running)
            {
                var parts = VisualTree.Descendants(running).OfType<FrameworkElement>().ToList();
                if (parts.FirstOrDefault(f => f.Name == "StepMessage") is { IsVisible: true } message)
                {
                    message.BringIntoView();
                }
                if (Keyboard.FocusedElement is not UIElement { IsVisible: true, IsEnabled: true })
                {
                    parts.OfType<ToggleButton>().FirstOrDefault()?.Focus();
                }
            }
            else if (ended is not null && Keys.ItemContainerGenerator.ContainerFromItem(ended) is UIElement { IsKeyboardFocusWithin: true } done)
            {
                // A saved key hands the keyboard to the next key's header, and leaves its own result open on screen.
                if (ended.NextKey is { } key && Keys.ItemContainerGenerator.ContainerFromItem(key) is DependencyObject following
                    && VisualTree.Descendants(following).OfType<ToggleButton>().FirstOrDefault() is { } header)
                {
                    header.Focus();
                    return;
                }
                // A result that asks for a step puts the keyboard on that step's button, so Enter does what the message says.
                var next = ended.NextStep switch
                {
                    CalibrationStep.Pressed => ended.SetPressed,
                    CalibrationStep.Learn => ended.Learn,
                    _ => null,
                };
                var buttons = VisualTree.Descendants(done).OfType<Button>().Where(b => b.IsVisible && b.IsEnabled).ToList();
                (buttons.FirstOrDefault(b => next is not null && b.Command == next) ?? buttons.FirstOrDefault())?.Focus();
            }
        });
    }
}
