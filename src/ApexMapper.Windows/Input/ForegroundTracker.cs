using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>
/// A <c>WINEVENT_OUTOFCONTEXT</c> foreground hook on its own thread with a message
/// loop. Each foreground change is resolved through <see cref="ForegroundResolver"/>,
/// the <see cref="ForegroundFlag"/> the hook reads is updated, and <see cref="Changed"/>
/// is raised on this thread when the game gained or lost focus. A 250 ms timer on the
/// same thread re-resolves the current foreground window, so one dropped event cannot
/// leave the flag stuck. Setting <see cref="GamePath"/> re-resolves too, so a game
/// chosen after it is already in front is picked up. One tracker per process.
///
/// Order on a transition: on gain <see cref="Changed"/> runs before the flag flips, so
/// the session's gate sweep finishes before the hook swallows anything; on loss the
/// flag drops first, so no key is swallowed after the game is gone. A throwing
/// <see cref="Changed"/> handler is counted in <see cref="HandlerFaults"/>, never
/// propagated.
/// </summary>
public sealed unsafe class ForegroundTracker : IForegroundSource
{
    public const int ReevaluateMs = 250;

    /// <summary>How long <see cref="Stop"/> waits for the tracker thread before giving up on it.</summary>
    public const int JoinTimeoutMs = 2000;

    private const uint WM_REEVALUATE = User32.WM_APP + 1;

    private static ForegroundTracker? s_current;

    private readonly IWindowSystem _windows;
    private readonly ForegroundFlag _flag;
    private readonly ManualResetEventSlim _ready = new(false);
    private ForegroundInfo _current = ForegroundInfo.None;
    private string? _gamePath;
    private int _handlerFaults;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Exception? _error;

    /// <summary>Raised on the tracker thread when the game gained or lost focus, with the new state. May call <see cref="Stop"/>.</summary>
    public event Action<ForegroundInfo>? Changed;

    public ForegroundTracker(ForegroundFlag flag, IWindowSystem? windows = null)
    {
        _flag = flag;
        _windows = windows ?? Win32WindowSystem.Instance;
    }

    /// <summary>Full path of the selected game's executable; null tracks nothing.</summary>
    public string? GamePath
    {
        get => Volatile.Read(ref _gamePath);
        set
        {
            Volatile.Write(ref _gamePath, value);
            if (_thread is { IsAlive: true })
            {
                User32.PostThreadMessageW(_threadId, WM_REEVALUATE, 0, 0);
            }
        }
    }

    /// <summary>The last resolved foreground window.</summary>
    public ForegroundInfo Current => Volatile.Read(ref _current);

    /// <summary>Exceptions caught on the tracker thread: <see cref="Changed"/> handlers and the resolution path.</summary>
    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    /// <summary>What ended the tracker thread early, if anything did; null while it runs or after a clean stop.</summary>
    public Exception? Error => Volatile.Read(ref _error);

    public bool IsRunning => _thread is { IsAlive: true } && Volatile.Read(ref _hook) != 0;

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The tracker was already started.");
        }
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one foreground tracker can exist per process.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-foreground" };
        _thread.Start();
        _ready.Wait();
        if (_error is not null)
        {
            _thread.Join();
            throw _error;
        }
    }

    /// <summary>
    /// Posts quit; the WinEvent hook is removed and the flag cleared on the tracker
    /// thread. From any other thread this joins, bounded by <see cref="JoinTimeoutMs"/>,
    /// and returns whether the thread has exited. From the tracker thread itself (a
    /// <see cref="Changed"/> handler) it returns false at once and the thread exits
    /// after the current message.
    /// </summary>
    public bool Stop()
    {
        var thread = _thread;
        if (thread is null || !thread.IsAlive)
        {
            return true;
        }
        for (var attempt = 0; attempt < 20 && !User32.PostThreadMessageW(_threadId, User32.WM_QUIT, 0, 0); attempt++)
        {
            Thread.Sleep(5);
        }
        if (thread.ManagedThreadId == Environment.CurrentManagedThreadId)
        {
            return false;
        }
        return thread.Join(JoinTimeoutMs);
    }

    public void Dispose()
    {
        if (Stop())
        {
            _ready.Dispose();
        }
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        var hook = User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, &WinEventProc, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
        if (hook == 0)
        {
            _error = new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed.");
            Interlocked.CompareExchange(ref s_current, null, this);
            _ready.Set();
            return;
        }
        Volatile.Write(ref _hook, hook);
        nuint timer = 0;
        try
        {
            Evaluate(User32.GetForegroundWindow());
            timer = User32.SetTimer(0, 0, ReevaluateMs, 0);
            _ready.Set();
            while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd == 0 && msg.message is WM_REEVALUATE or User32.WM_TIMER)
                {
                    Evaluate(User32.GetForegroundWindow());
                    continue;
                }
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e);
        }
        finally
        {
            if (timer != 0)
            {
                User32.KillTimer(0, timer);
            }
            User32.UnhookWinEvent(hook);
            Volatile.Write(ref _hook, 0);
            _flag.IsGameForeground = false;
            Interlocked.CompareExchange(ref s_current, null, this);
            _ready.Set();
        }
    }

    /// <summary>Resolves one window and publishes the result. Tracker thread, or a test driving it directly.</summary>
    internal void Evaluate(nint window)
    {
        try
        {
            var info = ForegroundResolver.Resolve(_windows, window, GamePath);
            var before = _current.GameHasFocus;
            Volatile.Write(ref _current, info);
            if (info.GameHasFocus == before)
            {
                _flag.IsGameForeground = info.GameHasFocus;
                return;
            }
            if (info.GameHasFocus)
            {
                Raise(info);
                _flag.IsGameForeground = true;
            }
            else
            {
                _flag.IsGameForeground = false;
                Raise(info);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    private void Raise(ForegroundInfo info)
    {
        try
        {
            Changed?.Invoke(info);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void WinEventProc(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (eventId == User32.EVENT_SYSTEM_FOREGROUND && objectId == 0)
        {
            s_current?.Evaluate(window);
        }
    }
}
