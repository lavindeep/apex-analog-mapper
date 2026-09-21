using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>Callback timing over the last <see cref="KeyboardHook.DurationRing"/> events.</summary>
public readonly record struct CallbackStats(int Count, double P50Ms, double P99Ms, double P999Ms, double MaxMs, int OverOneMs);

/// <summary>
/// The low-level keyboard hook on its own thread with its own message loop, above
/// normal priority. The callback reads <c>KBDLLHOOKSTRUCT</c> through a pointer, writes
/// the key's digital state to the store, asks <see cref="HookPolicy"/> whether to
/// swallow, and reads the cached foreground flag. It never calls win32k, takes no
/// lock, and allocates nothing. Callback durations go into a ring for off-thread
/// analysis. A 50 ms timer on the same thread runs <see cref="Timer"/> (the watchdog
/// and controller-presence checks in stage 3). Ctrl+Alt+F12 raises
/// <see cref="StopRequested"/> on this thread. One hook per process.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    public const int TimerMs = 50;
    public const int DurationRing = 4096;

    private static KeyboardHook? s_current;

    private readonly KeyStateStore _store;
    private readonly HookPolicy _policy;
    private readonly ForegroundFlag _foreground;
    private readonly long[] _durations = new long[DurationRing];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ScanCode[] _mapped;
    private int _durationNext;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Exception? _startError;

    /// <summary>Raised on the hook thread when Ctrl+Alt+F12 is pressed. Must be cheap.</summary>
    public Action? StopRequested { get; set; }

    /// <summary>Raised on the hook thread every 50 ms. Must be cheap.</summary>
    public Action? Timer { get; set; }

    /// <param name="mapped">Keys the session blocks; also the keys checked for being physically down at install.</param>
    public KeyboardHook(KeyStateStore store, HookPolicy policy, ForegroundFlag foreground, IEnumerable<ScanCode> mapped)
    {
        _store = store;
        _policy = policy;
        _foreground = foreground;
        _mapped = mapped.ToArray();
        _policy.SetMapped(_mapped);
    }

    public bool IsInstalled => _hook != 0;

    /// <summary>Installs the hook on a new thread and blocks until it is in place. Throws when Windows refuses.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The hook was already started.");
        }
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one keyboard hook can exist per process.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            _thread.Join();
            Interlocked.Exchange(ref s_current, null);
            throw _startError;
        }
    }

    /// <summary>Posts quit to the hook thread and joins; the hook is removed on that thread.</summary>
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

    /// <summary>Percentiles of the recorded callback durations. Allocates; call off the hot path.</summary>
    public CallbackStats Snapshot()
    {
        var count = Math.Min(Volatile.Read(ref _durationNext), DurationRing);
        if (count == 0)
        {
            return default;
        }
        var ms = new double[count];
        var over = 0;
        for (var i = 0; i < count; i++)
        {
            ms[i] = Volatile.Read(ref _durations[i]) * 1000d / Stopwatch.Frequency;
            if (ms[i] > 1)
            {
                over++;
            }
        }
        Array.Sort(ms);
        return new CallbackStats(count, At(ms, 0.5), At(ms, 0.99), At(ms, 0.999), ms[^1], over);

        static double At(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        _hook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &Callback, Kernel32.GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            _startError = new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed.");
            _ready.Set();
            return;
        }
        MarkKeysAlreadyDown();
        var timer = User32.SetTimer(0, 0, TimerMs, 0);
        _ready.Set();
        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == User32.WM_TIMER && msg.hwnd == 0)
            {
                Timer?.Invoke();
                continue;
            }
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }
        if (timer != 0)
        {
            User32.KillTimer(0, timer);
        }
        User32.UnhookWindowsHookEx(_hook);
        _hook = 0;
        _policy.Reset();
        Interlocked.Exchange(ref s_current, null);
    }

    /// <summary>Install time, before the callback can run: a mapped key already held must pass through until released.</summary>
    private void MarkKeysAlreadyDown()
    {
        foreach (var key in _mapped)
        {
            var vk = User32.MapVirtualKeyW(key.Value, User32.MAPVK_VSC_TO_VK_EX);
            if (vk == 0)
            {
                continue;
            }
            if ((User32.GetAsyncKeyState((int)vk) & 0x8000) != 0)
            {
                _policy.MarkDown(key.Slot);
                _store.SetDigital(key.Slot, true);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint Callback(int code, nint wParam, nint lParam)
    {
        var self = s_current;
        if (code < 0 || self is null)
        {
            return User32.CallNextHookEx(0, code, wParam, lParam);
        }
        var start = Stopwatch.GetTimestamp();
        var swallow = self.Handle((uint)wParam, (User32.KBDLLHOOKSTRUCT*)lParam);
        var index = self._durationNext;
        self._durations[index % DurationRing] = Stopwatch.GetTimestamp() - start;
        Volatile.Write(ref self._durationNext, index + 1);
        return swallow ? 1 : User32.CallNextHookEx(0, code, wParam, lParam);
    }

    private bool Handle(uint message, User32.KBDLLHOOKSTRUCT* key)
    {
        var down = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var injected = (key->flags & User32.LLKHF_INJECTED) != 0;
        var make = key->scanCode & 0xFF;
        if (make == 0)
        {
            return false;
        }
        var slot = (key->flags & User32.LLKHF_EXTENDED) != 0 ? 256 + (int)make : (int)make;
        if (!injected || _policy.SwallowInjected)
        {
            _store.SetDigital(slot, down);
        }
        if (_policy.IsStopChord(slot, down))
        {
            StopRequested?.Invoke();
            return true;
        }
        return _policy.Decide(slot, down, injected, _foreground.IsGameForeground);
    }
}
