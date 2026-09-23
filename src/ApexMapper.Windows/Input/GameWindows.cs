using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>A program with a window on the desktop, as the game card lists it.</summary>
/// <param name="Title">The title of its first window.</param>
/// <param name="ImagePath">Its executable, looking through a frame host to the app inside.</param>
/// <param name="Elevation">Whether the mapper's hook could see its input.</param>
public sealed record GameWindow(string Title, string ImagePath, Elevation Elevation);

/// <summary>
/// The programs a user could pick as the game: visible, titled top-level windows that
/// are on screen, one entry per executable, sorted by title. Tool windows, owned
/// windows (dialogs), cloaked windows (a suspended Store app keeps a "visible" frame)
/// and this app's own window are left out.
/// </summary>
public static unsafe class GameWindows
{
    public static IReadOnlyList<GameWindow> List() =>
        From(Win32WindowSystem.Instance, TopLevelWindows(), (uint)Environment.ProcessId);

    /// <summary>The pure part: windows already filtered to candidates, resolved to one entry per executable.</summary>
    internal static IReadOnlyList<GameWindow> From(IWindowSystem windows, IEnumerable<(nint Window, string Title)> candidates, uint ownProcessId)
    {
        var byPath = new Dictionary<string, GameWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var (window, title) in candidates)
        {
            var (pid, path, _) = ForegroundResolver.OwnerOf(windows, window);
            if (pid == 0 || pid == ownProcessId || path is null || byPath.ContainsKey(path))
            {
                continue;
            }
            byPath[path] = new GameWindow(title, path, ForegroundResolver.ElevationOf(windows, pid));
        }
        return byPath.Values.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static List<(nint, string)> TopLevelWindows()
    {
        var found = new List<(nint, string)>();
        var handle = GCHandle.Alloc(found);
        try
        {
            User32.EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindow(nint window, nint state)
    {
        if (IsCandidate(window) && TitleOf(window) is { } title)
        {
            ((List<(nint, string)>)GCHandle.FromIntPtr(state).Target!).Add((window, title));
        }
        return 1;
    }

    private static bool IsCandidate(nint window)
    {
        if (!User32.IsWindowVisible(window) || User32.GetWindow(window, User32.GW_OWNER) != 0)
        {
            return false;
        }
        if ((User32.GetWindowLongPtrW(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }
        uint cloaked = 0;
        return Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_CLOAKED, &cloaked, sizeof(uint)) != 0 || cloaked == 0;
    }

    private static string? TitleOf(nint window)
    {
        var length = User32.GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return null;
        }
        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            var copied = User32.GetWindowTextW(window, text, buffer.Length);
            return copied > 0 ? new string(text, 0, copied) : null;
        }
    }
}
