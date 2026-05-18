using System.Runtime.InteropServices;

namespace ApexMapper.App.Native;

/// <summary>
/// Minimal P/Invoke surface for the foreground-window watcher.
/// Uses [DllImport] rather than [LibraryImport] because ApexMapper.App.csproj
/// does not enable AllowUnsafeBlocks, which [LibraryImport] requires for
/// some of these marshalling shapes.
/// </summary>
internal static class WinEventInterop
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint EVENT_OBJECT_NAMECHANGE  = 0x800C;

    internal const uint WINEVENT_OUTOFCONTEXT    = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS  = 0x0002;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // -----------------------------------------------------------------------
    // Delegate
    // -----------------------------------------------------------------------

    /// <summary>
    /// Delegate matching the WinEventProc callback signature.
    /// Callers MUST keep a live reference to this delegate for as long as the
    /// hook is active — do not allow it to be collected by the GC.
    /// </summary>
    internal delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint   eventType,
        IntPtr hwnd,
        int    idObject,
        int    idChild,
        uint   idEventThread,
        uint   dwmsEventTime);

    // -----------------------------------------------------------------------
    // Hook management
    // -----------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = false)]
    internal static extern IntPtr SetWinEventHook(
        uint            eventMin,
        uint            eventMax,
        IntPtr          hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint            idProcess,
        uint            idThread,
        uint            dwFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // -----------------------------------------------------------------------
    // Window / process queries
    // -----------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = false)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    /// <param name="lpdwSize">
    /// On entry: the size of <paramref name="lpExeName"/> in characters.
    /// On exit: the number of characters written (excluding the null terminator).
    /// </param>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint   dwFlags,
        System.Text.StringBuilder lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
