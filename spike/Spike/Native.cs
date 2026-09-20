using System.Runtime.InteropServices;

namespace Spike;

// Throwaway P/Invoke for the measurement spike. Not the shape the real Windows
// layer will use; this only needs to work on this one PC.
internal static unsafe class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_QUIT = 0x0012;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint INFINITE = 0xFFFFFFFF;

    public delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint CreateWaitableTimerExW(nint lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(nint hTimer, ref long lpDueTime, int lPeriod, nint pfnCompletionRoutine, nint lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    // F13 (virtual key 0x7C, scan code 0x64): no application uses it, so a synthetic
    // press that is not swallowed lands harmlessly in whatever window has focus.
    public const uint TestVk = 0x7C;

    public static void SendTestKey(bool down)
    {
        var input = new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT { wVk = (ushort)TestVk, wScan = 0x64, dwFlags = down ? 0u : 0x2u },
        };
        SendInput(1, &input, sizeof(INPUT));
    }
}

// A 1 ms periodic wait using the high-resolution waitable timer.
internal sealed class HighResTimer : IDisposable
{
    private readonly nint _handle;

    public HighResTimer(int periodMs)
    {
        _handle = Native.CreateWaitableTimerExW(0, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_handle == 0)
        {
            throw new InvalidOperationException("CreateWaitableTimerEx failed: " + Marshal.GetLastWin32Error());
        }
        long due = -periodMs * 10_000L;
        if (!Native.SetWaitableTimer(_handle, ref due, periodMs, 0, 0, false))
        {
            throw new InvalidOperationException("SetWaitableTimer failed: " + Marshal.GetLastWin32Error());
        }
    }

    public void WaitNext() => Native.WaitForSingleObject(_handle, Native.INFINITE);

    public void Dispose() => Native.CloseHandle(_handle);
}

// A WH_KEYBOARD_LL hook on its own thread. The callback records W down
// timestamps and optionally swallows W (including injected events, for the
// kill test).
internal sealed class KeyHook : IDisposable
{
    private readonly Native.HookProc _proc;
    private readonly bool _swallowW;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _hook;
    private uint _threadId;

    public long LastWDownTicks;
    public int WDownCount;
    public int WSeenCount;

    // Down timestamps and counts for W, A, S, D (virtual keys 0x57, 0x41, 0x53, 0x44).
    public readonly long[] LastDownTicks = new long[256];
    public readonly int[] DownCounts = new int[256];

    public KeyHook(bool swallowW)
    {
        _swallowW = swallowW;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "spike-hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            throw new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }
        _ready.Set();
        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        Native.UnhookWindowsHookEx(_hook);
    }

    private unsafe nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var k = (Native.KBDLLHOOKSTRUCT*)lParam;
            if ((int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN && k->vkCode < 256)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                Volatile.Write(ref LastDownTicks[k->vkCode], now);
                Interlocked.Increment(ref DownCounts[k->vkCode]);
                if (k->vkCode == 0x57)
                {
                    Volatile.Write(ref LastWDownTicks, now);
                    Interlocked.Increment(ref WDownCount);
                }
            }
            if (k->vkCode == Native.TestVk)
            {
                Interlocked.Increment(ref WSeenCount);
                if (_swallowW)
                {
                    return 1;
                }
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
        _thread.Join(2000);
    }
}
