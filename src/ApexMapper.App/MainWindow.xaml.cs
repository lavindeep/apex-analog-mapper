using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ApexMapper.App.ViewModels;
using ApexMapper.App.Views;
using Wpf.Ui.Controls;

namespace ApexMapper.App;

/// <summary>
/// The one window: Home, and a page for each of the keyboard, profile, calibration and
/// settings, one at a time in a scrolling column. While the profile page waits for a key,
/// the window swallows key presses so Space or Enter cannot also press a button; the key
/// itself arrives through Raw Input. Losing focus cancels the wait, since Raw Input also
/// sees keys typed into other windows.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private MainViewModel? _main;
    private IInputElement? _openedFrom;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTemplateButtons();
    }

    /// <summary>WPF-UI's title bar leaves its buttons without a name for a screen reader, so each says what it does.</summary>
    private void NameTemplateButtons()
    {
        foreach (var element in VisualTree.Descendants(this))
        {
            if (element is TitleBarButton button)
            {
                BindingOperations.SetBinding(button, AutomationProperties.NameProperty, new Binding(nameof(TitleBarButton.ButtonType)) { Source = button });
            }
        }
    }

    public void Show(MainViewModel main)
    {
        _main = main;
        DataContext = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Page))
            {
                OnPageChanged(main.Page);
            }
        };
        Show();
    }

    /// <summary>
    /// A page opens at its top with focus on its back arrow, so the keyboard does not stay on
    /// a control the page hid. Going back Home returns focus to whatever opened the page,
    /// or to Home's first control when that one is gone, such as Calibrate once every key is.
    /// </summary>
    private void OnPageChanged(Page page)
    {
        Scroller.ScrollToTop();
        IInputElement? target;
        if (page == Page.Home)
        {
            target = _openedFrom;
            _openedFrom = null;
        }
        else
        {
            _openedFrom ??= Keyboard.FocusedElement;
            target = BackButton;
        }
        // Once the page is laid out, since a collapsed control cannot take focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (target?.Focus() != true && page == Page.Home)
            {
                Home.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }

    /// <summary>
    /// The page header's Stop hides once mapping stops, and Save and Discard once the
    /// profile is saved or put back, so the keyboard moves to the back arrow instead of nowhere.
    /// </summary>
    private void OnHeaderActionHidden(object sender, DependencyPropertyChangedEventArgs e)
    {
        // A button turned off first, as Save is while mapping stops, has already lost focus.
        if (e.NewValue is false && (sender is UIElement { IsKeyboardFocusWithin: true } || Keyboard.FocusedElement is not UIElement { IsVisible: true, IsEnabled: true }))
        {
            BackButton.Focus();
        }
    }

    /// <summary>Another launch asked for the window. One that asked just as this window closed is too late.</summary>
    public void BringForward()
    {
        if (_closed)
        {
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_main?.Profile.IsCapturing == true)
        {
            e.Handled = true;
            return;
        }
        var altLeft = e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt;
        if ((altLeft || e.Key == Key.BrowserBack) && _main is { Page: not Page.Home })
        {
            _main.GoHome.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>The mouse's back button goes back to Home, as in a browser. Alt+Left does it from the keyboard.</summary>
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && _main is { Page: not Page.Home })
        {
            _main.GoHome.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewMouseUp(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _main?.OnActivated();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _main?.OnDeactivated();
    }
}
