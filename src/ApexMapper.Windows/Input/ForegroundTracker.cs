using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>
/// A <c>WINEVENT_OUTOFCONTEXT</c> foreground hook on its own thread with a message
/// loop. Each foreground change is resolved through <see cref="ForegroundResolver"/>,
/// the <see cref="ForegroundFlag"/> the hook reads is updated, and <see cref="Changed"/>
/// is raised on this thread when the game gained or lost focus. Setting
/// <see cref="GamePath"/> re-resolves the current foreground window, so a game chosen
/// after it is already in front is picked up. One tracker per process.
/// </summary>
public sealed unsafe class ForegroundTracker : IDisposable
{
    private const uint WM_REEVALUATE = User32.WM_APP + 1;

    private static ForegroundTracker? s_current;

    private readonly IWindowSystem _windows;
    private readonly ForegroundFlag _flag;
    private readonly ManualResetEventSlim _ready = new(false);
    private ForegroundInfo _current = ForegroundInfo.None;
    private string? _gamePath;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Exception? _startError;

    /// <summary>Raised on the tracker thread when the game gained or lost focus, with the new state.</summary>
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
            if (_thread is not null)
            {
                User32.PostThreadMessageW(_threadId, WM_REEVALUATE, 0, 0);
            }
        }
    }

    /// <summary>The last resolved foreground window.</summary>
    public ForegroundInfo Current => Volatile.Read(ref _current);

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
        if (_startError is not null)
        {
            _thread.Join();
            Interlocked.Exchange(ref s_current, null);
            throw _startError;
        }
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }
        User32.PostThreadMessageW(_threadId, User32.WM_QUIT, 0, 0);
        _thread.Join();
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        _hook = User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, &WinEventProc, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
        if (_hook == 0)
        {
            _startError = new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed.");
            _ready.Set();
            return;
        }
        Evaluate(User32.GetForegroundWindow());
        _ready.Set();
        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_REEVALUATE && msg.hwnd == 0)
            {
                Evaluate(User32.GetForegroundWindow());
                continue;
            }
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }
        User32.UnhookWinEvent(_hook);
        _hook = 0;
        _flag.IsGameForeground = false;
        Interlocked.Exchange(ref s_current, null);
    }

    private void Evaluate(nint window)
    {
        var info = ForegroundResolver.Resolve(_windows, window, GamePath);
        var before = _current.GameHasFocus;
        Volatile.Write(ref _current, info);
        _flag.IsGameForeground = info.GameHasFocus;
        if (before != info.GameHasFocus)
        {
            Changed?.Invoke(info);
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
