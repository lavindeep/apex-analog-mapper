using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>The window queries the resolver needs, so a fake window tree can drive the tests.</summary>
public interface IWindowSystem
{
    /// <summary>Owning process id, or 0.</summary>
    uint ProcessIdOf(nint window);

    /// <summary>Full executable path, or null when the process cannot be queried.</summary>
    string? ImagePathOf(uint processId);

    /// <summary>The <c>Windows.UI.Core.CoreWindow</c> child of a frame host window, or 0.</summary>
    nint CoreWindowChildOf(nint window);

    /// <summary>Whether the process runs elevated; null when its token cannot be read.</summary>
    bool? IsElevated(uint processId);

    /// <summary>Whether this process runs elevated; null when its own token cannot be read.</summary>
    bool? IsCurrentProcessElevated();
}

/// <summary>Whether a low-level hook in this process can see the game's input.</summary>
public enum Elevation
{
    /// <summary>The game is not elevated, or we are too.</summary>
    Visible,

    /// <summary>The game is elevated and we are not: its input never reaches our hook. Run the mapper as administrator.</summary>
    Elevated,

    /// <summary>The game's token could not be read, so nothing is known; treated as invisible.</summary>
    Unknown,
}

/// <summary>What the foreground window resolved to.</summary>
/// <param name="Unwrapped">The window belonged to ApplicationFrameHost and the CoreWindow owner was used instead.</param>
/// <param name="Elevation">Only meaningful when <paramref name="IsGame"/>; <see cref="Elevation.Visible"/> otherwise.</param>
public sealed record ForegroundInfo(nint Window, uint ProcessId, string? ImagePath, bool IsGame, Elevation Elevation, bool Unwrapped)
{
    public static readonly ForegroundInfo None = new(0, 0, null, false, Elevation.Visible, false);

    /// <summary>Foreground and visible: the flag the hook reads.</summary>
    public bool GameHasFocus => IsGame && Elevation == Elevation.Visible;
}

/// <summary>
/// Pure resolution of a foreground window to "is it the selected game". Matches by
/// executable path, so a restarted game with a new process id keeps matching. Game
/// Pass and Store builds of Forza run under ApplicationFrameHost; the CoreWindow
/// child's owner is the game. Elevation is relative: an elevated game is invisible to
/// a hook only when this process is not elevated too, which is what running the mapper
/// as administrator fixes.
/// </summary>
public static class ForegroundResolver
{
    public const string FrameHostExecutable = "ApplicationFrameHost.exe";

    public static ForegroundInfo Resolve(IWindowSystem windows, nint window, string? gamePath)
    {
        if (window == 0)
        {
            return ForegroundInfo.None;
        }
        var (pid, path, unwrapped) = OwnerOf(windows, window);
        var isGame = gamePath is not null && path is not null && string.Equals(path, gamePath, StringComparison.OrdinalIgnoreCase);
        var elevation = isGame ? ElevationOf(windows, pid) : Elevation.Visible;
        return new ForegroundInfo(window, pid, path, isGame, elevation, unwrapped);
    }

    /// <summary>The process that owns a window and its executable, looking through a frame host to the app inside.</summary>
    internal static (uint ProcessId, string? ImagePath, bool Unwrapped) OwnerOf(IWindowSystem windows, nint window)
    {
        var pid = windows.ProcessIdOf(window);
        var path = pid == 0 ? null : windows.ImagePathOf(pid);
        if (path is not null && string.Equals(Path.GetFileName(path), FrameHostExecutable, StringComparison.OrdinalIgnoreCase))
        {
            var core = windows.CoreWindowChildOf(window);
            var corePid = core == 0 ? 0 : windows.ProcessIdOf(core);
            if (corePid != 0)
            {
                return (corePid, windows.ImagePathOf(corePid), true);
            }
        }
        return (pid, path, false);
    }

    /// <summary>Whether this process's hook can see the input of the given process.</summary>
    internal static Elevation ElevationOf(IWindowSystem windows, uint processId) => windows.IsElevated(processId) switch
    {
        null => Elevation.Unknown,
        true when windows.IsCurrentProcessElevated() != true => Elevation.Elevated,
        _ => Elevation.Visible,
    };
}

/// <summary>The real window system.</summary>
public sealed unsafe class Win32WindowSystem : IWindowSystem
{
    public static readonly Win32WindowSystem Instance = new();

    private bool? _ownElevation;
    private bool _ownElevationKnown;

    public uint ProcessIdOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    public string? ImagePathOf(uint processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }
        try
        {
            const int Capacity = 1024;
            var buffer = stackalloc char[Capacity];
            uint size = Capacity;
            return Kernel32.QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    public nint CoreWindowChildOf(nint window) => User32.FindWindowExW(window, 0, "Windows.UI.Core.CoreWindow", null);

    /// <summary>Read once: a process cannot change its elevation.</summary>
    public bool? IsCurrentProcessElevated()
    {
        if (!Volatile.Read(ref _ownElevationKnown))
        {
            _ownElevation = IsElevated((uint)Environment.ProcessId);
            Volatile.Write(ref _ownElevationKnown, true);
        }
        return _ownElevation;
    }

    public bool? IsElevated(uint processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }
        try
        {
            if (!Advapi32.OpenProcessToken(process, Advapi32.TOKEN_QUERY, out var token))
            {
                return null;
            }
            try
            {
                uint elevation = 0;
                return Advapi32.GetTokenInformation(token, Advapi32.TokenElevation, &elevation, sizeof(uint), out _) ? elevation != 0 : null;
            }
            finally
            {
                Kernel32.CloseHandle(token);
            }
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }
}
