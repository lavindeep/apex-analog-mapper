using ApexMapper.App.Native;

namespace ApexMapper.App.Services;

// ---------------------------------------------------------------------------
// Value type
// ---------------------------------------------------------------------------

/// <summary>Raw data from a WinEvent foreground notification.</summary>
public readonly record struct WindowFocusEvent(
    IntPtr       Hwnd,
    uint         ProcessId,
    DateTimeOffset Timestamp);

// ---------------------------------------------------------------------------
// Abstraction
// ---------------------------------------------------------------------------

public interface IWindowEventSource : IDisposable
{
    event EventHandler<WindowFocusEvent>? FocusChanged;
    void Start();
    void Stop();
}

// ---------------------------------------------------------------------------
// Win32 implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Installs a WinEvent hook for EVENT_SYSTEM_FOREGROUND so that every time
/// a new window is brought to the foreground a <see cref="FocusChanged"/> event
/// is raised with the target HWND and its owning process id.
/// </summary>
/// <remarks>
/// The <see cref="WinEventInterop.WinEventDelegate"/> field is intentionally
/// stored as an instance field so the GC can never collect it while the hook
/// is still registered — matching the pattern used by RawInputAdapter.
/// </remarks>
public sealed class WindowEventSink : IWindowEventSource
{
    // GC root: must outlive the hook handle.
    private readonly WinEventInterop.WinEventDelegate _winEventProc;

    private readonly object _lock = new();
    private IntPtr   _hookHandle = IntPtr.Zero;
    private bool     _disposed;

    public event EventHandler<WindowFocusEvent>? FocusChanged;

    public WindowEventSink()
    {
        // Assign to field — NOT to a local — so the GC root is established
        // before SetWinEventHook is called.
        _winEventProc = OnWinEvent;
    }

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hookHandle != IntPtr.Zero) return;

            _hookHandle = WinEventInterop.SetWinEventHook(
                WinEventInterop.EVENT_SYSTEM_FOREGROUND,
                WinEventInterop.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WinEventInterop.WINEVENT_OUTOFCONTEXT | WinEventInterop.WINEVENT_SKIPOWNPROCESS);

            // A zero handle means the hook was not installed — fail loudly rather
            // than leave a silently dead foreground watcher.
            if (_hookHandle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to install the foreground WinEvent hook.");
        }
    }

    public void Stop()
    {
        IntPtr hook;
        lock (_lock)
        {
            hook = _hookHandle;
            _hookHandle = IntPtr.Zero;
        }

        if (hook != IntPtr.Zero)
            WinEventInterop.UnhookWinEvent(hook);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    // -----------------------------------------------------------------------
    // Private
    // -----------------------------------------------------------------------

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint   eventType,
        IntPtr hwnd,
        int    idObject,
        int    idChild,
        uint   idEventThread,
        uint   dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;

        WinEventInterop.GetWindowThreadProcessId(hwnd, out var pid);

        var ev = new WindowFocusEvent(hwnd, pid, DateTimeOffset.UtcNow);
        FocusChanged?.Invoke(this, ev);
    }
}
