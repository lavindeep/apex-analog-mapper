using Velopack;

namespace ApexMapper.App;

// Entry point. Velopack must run before any WPF code so install, update, and
// uninstall hooks can exit early on the machine that launched them.
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
