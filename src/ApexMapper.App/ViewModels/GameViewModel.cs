using System.ComponentModel;
using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Windows.Input;

namespace ApexMapper.App.ViewModels;

/// <summary>One program in the game list.</summary>
/// <param name="Running">It has a window now; the remembered game is listed even when it does not.</param>
public sealed record GameItem(string Title, string ImagePath, Elevation Elevation, bool Running)
{
    public string FileName => Path.GetFileName(ImagePath);

    public string Detail => Running ? FileName : FileName + ", not running";
}

/// <summary>
/// The game card: the programs with a window, and the remembered game, which stays
/// listed and chosen while it is not running so Start works before the game is
/// launched. Until the remembered game shows up it looks again every
/// <see cref="RescanMs"/>, and the refresh button looks at once.
/// </summary>
public sealed class GameViewModel : ObservableObject
{
    public const int RescanMs = 90_000;

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private IReadOnlyList<GameItem> _games = [];
    private GameItem? _selected;
    private long _lastScan;

    public GameViewModel(AppServices services, Workspace workspace, string? remembered)
    {
        _services = services;
        _workspace = workspace;
        _workspace.GamePath = remembered;
        Refresh = new Command(Rescan);
        _workspace.PropertyChanged += OnWorkspaceChanged;
    }

    public IReadOnlyList<GameItem> Games
    {
        get => _games;
        private set => Set(ref _games, value);
    }

    /// <summary>The chosen game. A null from the view (its list was replaced) is ignored.</summary>
    public GameItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected || _workspace.SessionActive)
            {
                return;
            }
            _selected = value;
            Raise(nameof(Selected));
            Raise(nameof(Warning));
            if (!string.Equals(_workspace.GamePath, value.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                _workspace.GamePath = value.ImagePath;
                _workspace.Remember(_services.Settings, s => s with { GamePath = value.ImagePath });
            }
        }
    }

    public bool CanChoose => !_workspace.SessionActive;

    public string? Hint => _selected is null ? "Start the game once so it shows up here, then choose it." : null;

    /// <summary>The hook cannot see an elevated game's input unless the mapper is elevated too (B10).</summary>
    public string? Warning => _selected?.Elevation switch
    {
        Elevation.Elevated => $"{_selected.Title} runs as administrator, so the mapper cannot see its keys. Close the mapper and run it as administrator.",
        Elevation.Unknown => $"Windows would not say whether {_selected.Title} runs as administrator. If the keys do not reach it, run the mapper as administrator.",
        _ => null,
    };

    public Command Refresh { get; }

    /// <summary>Looks again while the remembered game has not been seen running.</summary>
    public void Tick(long nowMs)
    {
        if (_selected is { Running: false } && nowMs - _lastScan >= RescanMs)
        {
            Rescan();
        }
    }

    public void Rescan()
    {
        _lastScan = _services.NowMs();
        IReadOnlyList<GameWindow> windows;
        try
        {
            windows = _services.ListWindows();
        }
        catch (Exception e)
        {
            _services.Log("Listing windows failed: " + e.Message);
            windows = [];
        }
        var items = windows.Select(w => new GameItem(w.Title, w.ImagePath, w.Elevation, Running: true)).ToList();
        var path = _workspace.GamePath;
        var chosen = path is null ? null : items.FirstOrDefault(i => string.Equals(i.ImagePath, path, StringComparison.OrdinalIgnoreCase));
        if (path is not null && chosen is null)
        {
            chosen = new GameItem(Path.GetFileNameWithoutExtension(path), path, Elevation.Visible, Running: false);
            items.Insert(0, chosen);
        }
        Games = items;
        _selected = chosen;
        Raise(nameof(Selected));
        Raise(nameof(Warning));
        Raise(nameof(Hint));
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.SessionActive))
        {
            Raise(nameof(CanChoose));
        }
    }
}
