using System.Windows;
using Wpf.Ui.Appearance;

namespace ApexMapper.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        new MainWindow().Show();
    }
}
