using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Timing;

/// <summary>
/// A periodic wait on the high-resolution waitable timer, the only method stage 0
/// found consistent (p99 under 2 ms at a 1 ms period) regardless of the timer
/// resolution Windows grants the process. <see cref="WaitNext"/> returns false once
/// <see cref="Stop"/> has been called, from any thread.
/// </summary>
public sealed unsafe class HighResolutionTimer : IDisposable
{
    private readonly nint _timer;
    private readonly nint _stop;
    private bool _disposed;

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

    /// <summary>Blocks until the next period elapses. False when stopped. The stop event is first so it wins when both are signalled.</summary>
    public bool WaitNext()
    {
        var handles = stackalloc nint[2] { _stop, _timer };
        return Kernel32.WaitForMultipleObjects(2, handles, false, Kernel32.INFINITE) == Kernel32.WAIT_OBJECT_0 + 1;
    }

    /// <summary>Releases a waiter at once and makes every later wait return false.</summary>
    public void Stop() => Kernel32.SetEvent(_stop);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        Kernel32.CloseHandle(_timer);
        Kernel32.CloseHandle(_stop);
    }
}
