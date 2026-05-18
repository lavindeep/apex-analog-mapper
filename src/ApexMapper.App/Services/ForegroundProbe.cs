using System.Text;
using ApexMapper.App.Native;
using ApexMapper.Core;

namespace ApexMapper.App.Services;

// ---------------------------------------------------------------------------
// Abstraction
// ---------------------------------------------------------------------------

public interface IForegroundProbe
{
    /// <summary>
    /// Resolves the hwnd/pid pair into a <see cref="ForegroundContext"/>.
    /// Returns <see langword="null"/> when the process cannot be opened or
    /// the window title cannot be read (e.g. the target has already exited).
    /// </summary>
    ForegroundContext? Resolve(IntPtr hwnd, uint processId);
}

// ---------------------------------------------------------------------------
// Win32 implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Resolves foreground window metadata using Win32 APIs.
/// </summary>
/// <remarks>
/// SteamAppId strategy: reading another process's environment block requires
/// NtQueryInformationProcess + ReadProcessMemory, which is fragile and needs
/// elevated privileges. Instead we read SteamAppId from the current process's
/// environment — this is sufficient when the mapper itself was launched by
/// Steam (the fast path). If the mapper was not launched by Steam the field is
/// left null.
/// </remarks>
public sealed class Win32ForegroundProbe : IForegroundProbe
{
    // Cached once at construction time: only meaningful when the App itself
    // was launched inside a Steam game session.
    private static readonly string? s_steamAppId =
        Environment.GetEnvironmentVariable("SteamAppId");

    public ForegroundContext? Resolve(IntPtr hwnd, uint processId)
    {
        if (hwnd == IntPtr.Zero || processId == 0)
            return null;

        var title   = ReadWindowTitle(hwnd);
        var exePath = ReadExecutablePath(processId);

        // If we cannot even open the process (already gone, or access denied),
        // skip the emission rather than raising an empty/misleading context.
        if (exePath is null)
            return null;

        return new ForegroundContext(
            ExecutablePath: exePath,
            WindowTitle:    title ?? string.Empty,
            ProcessId:      processId,
            SteamAppId:     s_steamAppId,
            ObservedAt:     DateTimeOffset.UtcNow);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static string? ReadWindowTitle(IntPtr hwnd)
    {
        var len = WinEventInterop.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;

        var sb = new StringBuilder(len + 1);
        WinEventInterop.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ReadExecutablePath(uint processId)
    {
        var hProcess = WinEventInterop.OpenProcess(
            WinEventInterop.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);

        if (hProcess == IntPtr.Zero)
            return null;

        try
        {
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            return WinEventInterop.QueryFullProcessImageNameW(hProcess, 0, sb, ref size)
                ? sb.ToString()
                : null;
        }
        finally
        {
            WinEventInterop.CloseHandle(hProcess);
        }
    }
}
