using System.Windows;
using System.Windows.Input;
using ApexMapper.App.ViewModels;
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

    public MainWindow()
    {
        InitializeComponent();
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

    /// <summary>Another launch asked for the window.</summary>
    public void BringForward()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
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
        _main?.Setup.Recheck();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _main?.Profile.CancelCapture.Execute(null);
    }
}
