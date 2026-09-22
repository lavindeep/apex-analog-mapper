using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Timing;

/// <summary>
/// A periodic wait on the high-resolution waitable timer, the only method stage 0
/// found consistent (p99 under 2 ms at a 1 ms period) regardless of the timer
/// resolution Windows grants the process. <see cref="WaitNext"/> returns false once
/// <see cref="Stop"/> has been called, from any thread. Windows reuses handle values
/// at once, so the owner must join the waiting thread before <see cref="Dispose"/>;
/// after it, every call is a no-op instead of touching whatever handle got the number.
/// </summary>
public sealed unsafe class HighResolutionTimer : IDisposable
{
    private nint _timer;
    private nint _stop;
    private int _disposed;

    public HighResolutionTimer(int periodMs)
    {
        if (periodMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(periodMs), periodMs, "Period must be at least 1 ms.");
        }
        _timer = Kernel32.CreateWaitableTimerExW(0, null, Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Kernel32.TIMER_ALL_ACCESS);
        if (_timer == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWaitableTimerEx failed.");
        }
        _stop = Kernel32.CreateEventW(0, bManualReset: true, bInitialState: false, null);
        if (_stop == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Kernel32.CloseHandle(_timer);
            throw new Win32Exception(error, "CreateEvent failed.");
        }
        var due = -periodMs * 10_000L;
        if (!Kernel32.SetWaitableTimer(_timer, in due, periodMs, 0, 0, false))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "SetWaitableTimer failed.");
        }
        PeriodMs = periodMs;
    }

    public int PeriodMs { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Blocks until the next period elapses. False when stopped or disposed. The stop event is first so it wins when both are signalled.</summary>
    public bool WaitNext()
    {
        if (IsDisposed)
        {
            return false;
        }
        var handles = stackalloc nint[2] { _stop, _timer };
        var result = Kernel32.WaitForMultipleObjects(2, handles, false, Kernel32.INFINITE);
        if (result == Kernel32.WAIT_FAILED)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForMultipleObjects failed.");
        }
        return result == Kernel32.WAIT_OBJECT_0 + 1;
    }

    /// <summary>Releases a waiter at once and makes every later wait return false.</summary>
    public void Stop()
    {
        if (!IsDisposed)
        {
            Kernel32.SetEvent(_stop);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Kernel32.SetEvent(_stop);
        var timer = Interlocked.Exchange(ref _timer, 0);
        var stop = Interlocked.Exchange(ref _stop, 0);
        Kernel32.CloseHandle(timer);
        Kernel32.CloseHandle(stop);
    }
}
