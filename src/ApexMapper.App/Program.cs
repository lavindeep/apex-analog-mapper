using Velopack;

namespace ApexMapper.App;

// Entry point. Velopack must run before any WPF code so install, update, and
// uninstall hooks can exit early on the machine that launched them.
public static class Program
{
    /// <summary>Passed by the copy that restarts the app, which is still closing when this one starts.</summary>
    public const string RestartedArgument = "--restarted";

    /// <summary>How long a restarted copy waits for the old one to exit before handing over to it instead.</summary>
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(30);

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        SingleInstance? claimed;
        try
        {
            claimed = SingleInstance.TryClaim(args.Contains(RestartedArgument) ? RestartWait : TimeSpan.Zero);
        }
        catch (UnauthorizedAccessException)
        {
            // A copy run as administrator holds the name, and this one may not even open it.
            System.Windows.MessageBox.Show("Apex Analog Mapper is already running as administrator. Switch to it from the taskbar.",
                "Apex Analog Mapper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return 0;
        }
        using var instance = claimed;
        if (instance is null)
        {
            return 0;
        }
        var app = new App(instance);
        app.InitializeComponent();
        return app.Run();
    }
}
