using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ApexMapper.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
