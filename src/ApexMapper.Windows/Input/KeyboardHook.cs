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
/// swallow, and reads the cached foreground flag. It reads no window state, makes no
/// blocking call, takes no lock, and allocates nothing; the pass-through path calls
/// <c>CallNextHookEx</c>, which is why the whole callback is timed. Durations go into a
/// ring for off-thread analysis. A 50 ms timer on the same thread resynchronises the
/// policy's modifier bits from the asynchronous key state and then runs
/// <see cref="Timer"/> (the watchdog and controller-presence checks in stage 3).
/// Ctrl+Alt+F12 raises <see cref="StopRequested"/> on this thread. One hook per process.
///
/// A mapped key whose down passes through (a chord, a key held at install, a press
/// after the game lost focus) is gated in the store as well as recorded down, so the
/// engine does not drive the pad from a press the game also received; the gate clears
/// on release, or at rest for an analog-driven key.
///
/// Exceptions never leave the hook thread: a throwing <see cref="StopRequested"/> or
/// <see cref="Timer"/> handler, or anything thrown on the callback path, is counted in
/// <see cref="HandlerFaults"/> and the event passes through. Windows removes a
/// low-level hook silently when a callback overruns its timeout; <see cref="EventCount"/>
/// and <see cref="LastEventTicks"/> exist so the session can compare them against the
/// Raw Input pump and notice.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    public const int TimerMs = 50;
    public const int DurationRing = 4096;

    /// <summary>How long <see cref="Stop"/> waits for the hook thread before giving up on it.</summary>
    public const int JoinTimeoutMs = 2000;

    private const int VK_PACKET = 0xE7;
    private const uint Overrun = 0xFF;
    private const uint LeftShift = 0x2A;
    private const uint RightShift = 0x36;
    private const uint NumLockOrPause = 0x45;
    private const int PauseSlot = 512 + 0x45;

    private static KeyboardHook? s_current;

    private readonly KeyStateStore _store;
    private readonly HookPolicy _policy;
    private readonly ForegroundFlag _foreground;
    private readonly long[] _durations = new long[DurationRing];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ScanCode[] _mapped;
    private long _eventCount;
    private long _lastEventTicks;
    private int _handlerFaults;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Exception? _error;

    /// <summary>Raised on the hook thread when Ctrl+Alt+F12 is pressed. Must be cheap. May call <see cref="Stop"/>.</summary>
    public Action? StopRequested { get; set; }

    /// <summary>Raised on the hook thread every 50 ms. Must be cheap. May call <see cref="Stop"/>.</summary>
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

    public bool IsInstalled => Volatile.Read(ref _hook) != 0;

    /// <summary>Callbacks seen since install, whether swallowed or passed.</summary>
    public long EventCount => Volatile.Read(ref _eventCount);

    /// <summary>Stopwatch timestamp of the last callback, or zero.</summary>
    public long LastEventTicks => Volatile.Read(ref _lastEventTicks);

    /// <summary>Exceptions caught on the hook thread: handlers and the callback path.</summary>
    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    /// <summary>What ended the hook thread early, if anything did; null while it runs or after a clean stop.</summary>
    public Exception? Error => Volatile.Read(ref _error);

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
        if (_error is not null)
        {
            _thread.Join();
            throw _error;
        }
    }

    /// <summary>
    /// Posts quit to the hook thread; the hook is removed on that thread. From any other
    /// thread this joins, bounded by <see cref="JoinTimeoutMs"/>, and returns whether
    /// the thread has exited. From the hook thread itself (a <see cref="StopRequested"/>
    /// or <see cref="Timer"/> handler) it returns false at once and the thread exits
    /// after the current message; joining would wait for itself forever.
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

    /// <summary>Percentiles of the recorded callback durations. Allocates; call off the hot path.</summary>
    public CallbackStats Snapshot()
    {
        var count = (int)Math.Min(EventCount, DurationRing);
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

    /// <summary>
    /// The slot a hook event lands in, or -1 for events the mapper ignores: Unicode
    /// packets (<c>VK_PACKET</c>, whose scan code is a character), make codes outside a
    /// byte, the overrun code, and the fake shifts around extended keys. Make code 0x45
    /// is the one place the hook and Raw Input disagree: the hook reports NumLock with
    /// the extended flag and Pause without it, so both are normalised to Raw Input's
    /// slots (NumLock plain, Pause on the E1 page).
    /// </summary>
    internal static int SlotOf(uint vkCode, uint scanCode, uint flags)
    {
        if (vkCode == VK_PACKET || scanCode is 0 or > 0xFF || scanCode == Overrun)
        {
            return -1;
        }
        var extended = (flags & User32.LLKHF_EXTENDED) != 0;
        if (extended && scanCode is LeftShift or RightShift)
        {
            return -1;
        }
        if (scanCode == NumLockOrPause)
        {
            return extended ? (int)NumLockOrPause : PauseSlot;
        }
        return extended ? 256 + (int)scanCode : (int)scanCode;
    }

    /// <summary>The callback body, without the timing and the next-hook call. True to swallow.</summary>
    internal bool Handle(uint message, in User32.KBDLLHOOKSTRUCT key)
    {
        var slot = SlotOf(key.vkCode, key.scanCode, key.flags);
        if (slot < 0)
        {
            return false;
        }
        var down = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var injected = (key.flags & User32.LLKHF_INJECTED) != 0;
        if (_policy.IsStopChord(slot, down, injected))
        {
            _store.SetDigital(slot, down);
            Invoke(StopRequested);
            return true;
        }
        var swallow = _policy.Decide(slot, down, injected, _foreground.IsGameForeground);
        _store.SetDigital(slot, down);
        if (down && !swallow && _policy.IsMapped(slot))
        {
            _store.Gate(slot);
        }
        return swallow;
    }

    private void Invoke(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        var hook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &Callback, Kernel32.GetModuleHandleW(null), 0);
        if (hook == 0)
        {
            _error = new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed.");
            Interlocked.CompareExchange(ref s_current, null, this);
            _ready.Set();
            return;
        }
        Volatile.Write(ref _hook, hook);
        nuint timer = 0;
        try
        {
            SyncModifiers();
            MarkKeysAlreadyDown();
            timer = User32.SetTimer(0, 0, TimerMs, 0);
            _ready.Set();
            while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == User32.WM_TIMER && msg.hwnd == 0)
                {
                    OnTimer();
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
            User32.UnhookWindowsHookEx(hook);
            Volatile.Write(ref _hook, 0);
            _policy.Reset();
            Interlocked.CompareExchange(ref s_current, null, this);
            _ready.Set();
        }
    }

    private void OnTimer()
    {
        try
        {
            SyncModifiers();
            Timer?.Invoke();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    /// <summary>
    /// The asynchronous key state is the truth for modifiers whose key-up the hook never
    /// sees (Ctrl+Alt+Del, a UAC prompt, a modifier held at install). Read at install
    /// and on the timer, never in the callback, where it is not yet updated for the
    /// event being delivered. A timer tick that lands between a modifier's callback and
    /// Windows updating the state can drop that bit until the next tick.
    /// </summary>
    private void SyncModifiers()
    {
        var bits = Bit(User32.VK_LCONTROL, HookPolicy.LeftCtrlBit)
            | Bit(User32.VK_RCONTROL, HookPolicy.RightCtrlBit)
            | Bit(User32.VK_LMENU, HookPolicy.LeftAltBit)
            | Bit(User32.VK_RMENU, HookPolicy.RightAltBit)
            | Bit(User32.VK_LWIN, HookPolicy.LeftWinBit)
            | Bit(User32.VK_RWIN, HookPolicy.RightWinBit);
        _policy.SyncModifiers(bits);

        static int Bit(int vk, int bit) => (User32.GetAsyncKeyState(vk) & 0x8000) != 0 ? bit : 0;
    }

    /// <summary>
    /// Install time, before the callback can run: a mapped key already held must pass
    /// through until released, and is gated rather than recorded down because the game
    /// has it. The scan-code-to-virtual-key mapping folds numpad and arrow pairs onto
    /// one virtual key and has no answer for extended NumLock, so a held arrow also
    /// marks the matching numpad key when both are mapped; that costs one passed-through
    /// press of the other key.
    /// </summary>
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
                _store.Gate(key.Slot);
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
        var swallow = false;
        try
        {
            swallow = self.Handle((uint)wParam, in *(User32.KBDLLHOOKSTRUCT*)lParam);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref self._handlerFaults);
        }
        var result = swallow ? 1 : User32.CallNextHookEx(0, code, wParam, lParam);
        var end = Stopwatch.GetTimestamp();
        var index = self._eventCount;
        self._durations[index & (DurationRing - 1)] = end - start;
        Volatile.Write(ref self._lastEventTicks, end);
        Volatile.Write(ref self._eventCount, index + 1);
        return result;
    }
}
