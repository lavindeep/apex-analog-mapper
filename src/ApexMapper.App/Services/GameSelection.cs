using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ApexMapper.App.Services;

public sealed record GameProcess(int ProcessId, long ProcessStartTimeUtcTicks, string Title, string ExecutableName)
{
    public string DisplayName => $"{Title} · {ExecutableName}";
    public bool IsSameProcess(GameProcess? other) => other is not null
        && ProcessId == other.ProcessId && ProcessStartTimeUtcTicks == other.ProcessStartTimeUtcTicks;
}

public interface IGameSelection
{
    GameProcess? SelectedGame { get; }
    event EventHandler? Changed;
    void Select(GameProcess? game);
    string? ValidateSelectedGame(out GameProcess? game);
}

/// <summary>Keeps an explicit runtime selection and rejects exited or reused process IDs.</summary>
public sealed class GameSelection : IGameSelection
{
    private readonly object _sync = new();
    private readonly Func<int, long?> _readStartTime;
    private GameProcess? _selectedGame;

    public GameSelection(Func<int, long?>? readProcessStartTimeUtcTicks = null)
        => _readStartTime = readProcessStartTimeUtcTicks ?? ReadProcessStartTime;

    public GameProcess? SelectedGame { get { lock (_sync) return _selectedGame; } }
    public event EventHandler? Changed;

    public void Select(GameProcess? game)
    {
        bool changed;
        lock (_sync)
        {
            changed = !SameProcess(_selectedGame, game);
            _selectedGame = game;
        }
        // A changed window title does not change the selected process or stop mapping.
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? ValidateSelectedGame(out GameProcess? game)
    {
        const string chooseGame = "Choose a running game before starting.";
        game = null;
        var selected = SelectedGame;
        if (selected is null) return chooseGame;

        long? started;
        try { started = _readStartTime(selected.ProcessId); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            started = null;
        }

        lock (_sync)
        {
            if (!SameProcess(selected, _selectedGame)) return "Game changed. Start again.";
            if (started == selected.ProcessStartTimeUtcTicks)
            {
                game = _selectedGame;
                return null;
            }
            _selectedGame = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return chooseGame;
    }

    /// <summary>Lists visible application windows without opening their executable files.</summary>
    public static IReadOnlyList<GameProcess> EnumerateRunningGames()
    {
        var games = new List<GameProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited) continue;
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero || !IsWindowVisible(window)) continue;
                    var title = process.MainWindowTitle.Trim();
                    if (title.Length == 0) continue;
                    games.Add(new GameProcess(process.Id, process.StartTime.ToUniversalTime().Ticks,
                        title, process.ProcessName + ".exe"));
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Processes can exit or deny metadata access during enumeration.
                }
            }
        }
        return games;
    }

    private static long? ReadProcessStartTime(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
    }

    private static bool SameProcess(GameProcess? first, GameProcess? second)
        => first is null ? second is null : first.IsSameProcess(second);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
