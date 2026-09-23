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

        using var instance = SingleInstance.TryClaim(args.Contains(RestartedArgument) ? RestartWait : TimeSpan.Zero);
        if (instance is null)
        {
            return 0;
        }
        var app = new App(instance);
        app.InitializeComponent();
        return app.Run();
    }
}
