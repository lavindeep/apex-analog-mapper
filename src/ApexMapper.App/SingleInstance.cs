using System.Threading;
using ApexMapper.Windows.Input;

namespace ApexMapper.App;

/// <summary>
/// One copy of the app per Windows session. Two copies would poll the same keyboard and
/// read each other's replies. The first copy holds a named mutex for its lifetime; a
/// second launch signals a named event, which brings the first window forward, and
/// exits. The mutex belongs to the thread that claimed it, so the one that calls
/// <see cref="TryClaim"/> disposes it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ApexAnalogMapper";
    private const string ShowEventName = @"Local\ApexAnalogMapper.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>
    /// Claims the app for this process, waiting up to <paramref name="wait"/> for a copy
    /// that is closing. Null when another copy runs: it has been asked to show its window.
    /// </summary>
    public static SingleInstance? TryClaim(TimeSpan wait)
    {
        var mutex = new Mutex(false, MutexName);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The last copy ended without releasing it: a crash. The claim is ours.
            owned = true;
        }
        if (owned)
        {
            return new SingleInstance(mutex, show);
        }
        ForegroundPermission.GrantToAnyProcess();
        show.Set();
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Runs <paramref name="show"/> on a thread-pool thread each time another launch asks for the window.</summary>
    public void OnShowRequested(Action show) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
