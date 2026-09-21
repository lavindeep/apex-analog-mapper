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
}

/// <summary>What the foreground window resolved to.</summary>
/// <param name="Unwrapped">The window belonged to ApplicationFrameHost and the CoreWindow owner was used instead.</param>
/// <param name="Elevated">The game runs elevated (or its token is unreadable, which means the same for a hook): its input is invisible to us.</param>
public sealed record ForegroundInfo(nint Window, uint ProcessId, string? ImagePath, bool IsGame, bool Elevated, bool Unwrapped)
{
    public static readonly ForegroundInfo None = new(0, 0, null, false, false, false);

    /// <summary>Foreground and visible: the flag the hook reads.</summary>
    public bool GameHasFocus => IsGame && !Elevated;
}

/// <summary>
/// Pure resolution of a foreground window to "is it the selected game". Matches by
/// executable path, so a restarted game with a new process id keeps matching. Game
/// Pass and Store builds of Forza run under ApplicationFrameHost; the CoreWindow
/// child's owner is the game.
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
        var pid = windows.ProcessIdOf(window);
        var path = pid == 0 ? null : windows.ImagePathOf(pid);
        var unwrapped = false;
        if (path is not null && string.Equals(Path.GetFileName(path), FrameHostExecutable, StringComparison.OrdinalIgnoreCase))
        {
            var core = windows.CoreWindowChildOf(window);
            var corePid = core == 0 ? 0 : windows.ProcessIdOf(core);
            if (corePid != 0)
            {
                pid = corePid;
                path = windows.ImagePathOf(corePid);
                unwrapped = true;
            }
        }
        var isGame = gamePath is not null && path is not null && string.Equals(path, gamePath, StringComparison.OrdinalIgnoreCase);
        var elevated = isGame && (windows.IsElevated(pid) ?? true);
        return new ForegroundInfo(window, pid, path, isGame, elevated, unwrapped);
    }
}

/// <summary>The real window system.</summary>
public sealed unsafe class Win32WindowSystem : IWindowSystem
{
    public static readonly Win32WindowSystem Instance = new();

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
