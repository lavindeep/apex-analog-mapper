namespace ApexMapper.Windows.Session;

/// <summary>
/// The last line of defence while a session runs: an unhandled exception on any thread
/// ends the process, and before it does this runs the session's emergency shutdown
/// (zero and unplug the pad, remove the hook, restore the GC mode) on the failing
/// thread. Process death would unplug the pad and remove the hook anyway; this makes
/// the game see neutral first. Unobserved task exceptions do not end a .NET process, so
/// they are not handled here.
/// </summary>
internal sealed class CrashGuard : IDisposable
{
    private readonly Action _emergency;
    private int _disposed;

    public CrashGuard(Action emergency)
    {
        _emergency = emergency;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e) => Run();

    /// <summary>What the handler does. Internal so a test can drive it without ending its own process.</summary>
    internal void Run()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        try
        {
            _emergency();
        }
        catch (Exception)
        {
            // The process is ending; nothing is left to report to.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        }
    }
}
