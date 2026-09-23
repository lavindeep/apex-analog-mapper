using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;
using ApexMapper.App.ViewModels;
using ApexMapper.App.Views;
using Wpf.Ui.Controls;

namespace ApexMapper.App;

/// <summary>
/// The one window: six cards in a scrolling column. While the profile card waits for a
/// key, the window swallows key presses so Space or Enter cannot also press a button;
/// the key itself arrives through Raw Input. Losing focus cancels the wait, since Raw
/// Input also sees keys typed into other windows.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private MainViewModel? _main;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTemplateButtons();
    }

    /// <summary>
    /// WPF-UI's templates leave some buttons without a name for a screen reader: each
    /// card's expand button takes its card's name, and the title bar's buttons say what
    /// they do.
    /// </summary>
    private void NameTemplateButtons()
    {
        foreach (var element in VisualTree.Descendants(this))
        {
            if (element is CardExpander card && card.Template?.FindName("ExpanderToggleButton", card) is DependencyObject toggle)
            {
                AutomationProperties.SetName(toggle, AutomationProperties.GetName(card));
            }
            else if (element is TitleBarButton button)
            {
                BindingOperations.SetBinding(button, AutomationProperties.NameProperty, new Binding(nameof(TitleBarButton.ButtonType)) { Source = button });
            }
        }
    }

    public void Show(MainViewModel main)
    {
        _main = main;
        DataContext = main;
        main.CardRequested += card =>
        {
            if (card == main.Calibration)
            {
                CalibrationCard.BringIntoView();
            }
        };
        Show();
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
        base.OnPreviewKeyDown(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _main?.OnActivated();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _main?.Profile.CancelCapture.Execute(null);
    }
}
