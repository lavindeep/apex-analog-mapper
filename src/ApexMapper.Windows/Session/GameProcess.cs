using System.Diagnostics;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ApexMapper.Windows.Session;

/// <summary>The selected game while it runs.</summary>
public interface IGameProcess : IDisposable
{
    /// <summary>Registers the callback for when the game exits; runs at once if it already has. Raised on a thread-pool thread.</summary>
    void OnExit(Action exited);
}

/// <summary>
/// Every running process whose executable path is the selected game's, matched the way
/// the foreground tracker matches it. The game counts as exited when the last of them
/// has. A process that cannot be opened for waiting is left out, and if none can be the
/// game counts as not running. An elevated game opens fine from a process that is not
/// (checked on the maintainer's PC in stage 3).
/// </summary>
public sealed class GameProcess : IGameProcess
{
    private readonly List<(WaitHandle Handle, RegisteredWaitHandle? Wait)> _waits = [];
    private readonly Lock _lock = new();
    private int _running;
    private Action? _exited;
    private bool _disposed;

    private GameProcess()
    {
    }

    /// <summary>The running game, or null when no process has that path.</summary>
    public static IGameProcess? Find(string gamePath)
    {
        var pids = new List<uint>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(gamePath)))
        {
            using (process)
            {
                var pid = (uint)process.Id;
                if (string.Equals(Win32WindowSystem.Instance.ImagePathOf(pid), gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    pids.Add(pid);
                }
            }
        }
        return Watch(pids);
    }

    /// <summary>The processes that can be opened for waiting, or null when none can.</summary>
    internal static IGameProcess? Watch(IEnumerable<uint> processIds)
    {
        var game = new GameProcess();
        foreach (var pid in processIds)
        {
            var handle = Kernel32.OpenProcess(Kernel32.SYNCHRONIZE, false, pid);
            if (handle != 0)
            {
                game._waits.Add((new ProcessWaitHandle(handle), null));
            }
        }
        if (game._waits.Count == 0)
        {
            game.Dispose();
            return null;
        }
        return game;
    }

    public void OnExit(Action exited)
    {
        lock (_lock)
        {
            if (_exited is not null || _disposed)
            {
                throw new InvalidOperationException("An exit callback is already registered.");
            }
            _exited = exited;
            _running = _waits.Count;
            for (var i = 0; i < _waits.Count; i++)
            {
                var handle = _waits[i].Handle;
                _waits[i] = (handle, ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => OneExited(), null, Timeout.Infinite, executeOnlyOnce: true));
            }
        }
    }

    private void OneExited()
    {
        Action? exited;
        lock (_lock)
        {
            if (_disposed || --_running > 0)
            {
                return;
            }
            exited = _exited;
        }
        exited?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var (handle, wait) in _waits)
            {
                wait?.Unregister(null);
                handle.Dispose();
            }
            _waits.Clear();
        }
    }
}

/// <summary>A process handle to wait on; unlike a borrowed <see cref="ManualResetEvent"/>, it creates no event of its own.</summary>
internal sealed class ProcessWaitHandle : WaitHandle
{
    public ProcessWaitHandle(nint processHandle) => SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: true);
}