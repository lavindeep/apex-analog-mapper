using System.Runtime.InteropServices;
using ApexMapper.Logging;
using ApexMapper.Output.ViGEm;

namespace ApexMapper.Supervisor;

/// <summary>
/// Per-session supervisor process. The tray launches one instance with
/// <c>--session &lt;id&gt;</c>; it owns the virtual pad for that session and,
/// on any shutdown signal, zeroes and disconnects the pad before exiting.
/// With no connected session for a full idle window the server retires
/// itself and the process exits cleanly rather than lingering after the
/// tray exits; the tray respawns a supervisor on the next enable.
///
/// Only the argument parsing (<see cref="SupervisorArgs"/>) and the
/// single-instance decision are unit-tested; the composition below wires real
/// process, pipe, and driver IO and is exercised end-to-end on Windows rather
/// than in unit tests.
/// </summary>
internal static class Program
{
    private const long MaxLogBytes = 1024 * 1024;
    private const int MaxLogFiles = 5;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        if (!SupervisorArgs.TryParse(args, out var parsed, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: ApexMapper.Supervisor --session <id>");
            return 2;
        }

        var sessionId = parsed!.SessionId;

        // One supervisor per session: a second launch for the same session
        // defers to the first rather than fighting over the pad.
        using var singleInstance = new Mutex(
            initiallyOwned: true, $@"Local\ApexMapper.Supervisor.{sessionId}", out var createdNew);
        if (!createdNew)
        {
            Console.Out.WriteLine($"A supervisor for session '{sessionId}' is already running.");
            return 0;
        }

        using var log = CreateLog();
        log.Write(LogLevel.Info, $"Supervisor starting for session {sessionId}.");

        // Deliberately NOT `await using`: disposal re-awaits the accept loop with
        // no bound, which would undo the bounded stop below — a wedged loop must
        // end in a forced exit, never an unbounded wait.
        var server = new SupervisorServer(
            sessionId, () => new ViGEmXboxOutput(), new SupervisorOptions());
        server.Diagnostics += line => log.Write(LogLevel.Info, line);
        server.SessionEnded += reason => log.Write(LogLevel.Info, $"Session ended: {reason}.");

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            // Handle Ctrl+C ourselves so the pad is torn down cleanly instead of
            // the runtime hard-killing the process.
            e.Cancel = true;
            RequestShutdown(shutdown);
        };
        Console.CancelKeyPress += cancelHandler;
        using var sigterm = RegisterSigterm(shutdown);

        server.Start();
        log.Write(LogLevel.Info, "Supervisor accepting clients.");

        var run = server.Completion;
        try
        {
            // Wakes on whichever comes first: a shutdown signal, or the accept
            // loop retiring itself after its idle window.
            await run.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: a shutdown signal fired.
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        log.Write(
            LogLevel.Info,
            run.IsCompletedSuccessfully && run.Result == ServerExitReason.IdleTimeout
                ? "No client connected within the idle window; exiting."
                : "Supervisor stopping.");
        try
        {
            await server.StopAsync().WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The accept loop is wedged (a driver or pipe call ignoring
            // cancellation). Hanging here would leave a zombie process holding
            // the session mutex — blocking every supervisor relaunch for this
            // session — and swallowing further termination signals. Exit hard;
            // the OS reclaims the pipe and the driver drops the pad.
            log.Write(LogLevel.Warn, "Supervisor stop timed out; forcing exit.");
            log.Flush();
            Environment.Exit(1);
        }

        // The loop has fully unwound; disposal is now instantaneous.
        await server.DisposeAsync().ConfigureAwait(false);
        log.Write(LogLevel.Info, "Supervisor stopped.");
        log.Flush();
        return 0;
    }

    private static void RequestShutdown(CancellationTokenSource shutdown)
    {
        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shutting down; nothing to do.
        }
    }

    private static IDisposable? RegisterSigterm(CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                RequestShutdown(shutdown);
            });
        }
        catch (PlatformNotSupportedException)
        {
            // Ctrl+C still covers interactive shutdown where SIGTERM is unavailable.
            return null;
        }
    }

    private static LogStore CreateLog()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        var logDir = Path.Combine(baseDir, "ApexAnalogMapper", "logs");
        return new LogStore(logDir, "supervisor.log", MaxLogBytes, MaxLogFiles);
    }
}
