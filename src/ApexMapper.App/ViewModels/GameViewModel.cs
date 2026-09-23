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

    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() => $"{Title}, {Detail}";
}

/// <summary>
/// The game card: the programs with a window, and the remembered game, which stays
/// listed and chosen while it is not running so Start works before the game is
/// launched. Until a chosen game is seen running it looks again every
/// <see cref="RescanMs"/> and whenever the window comes back to the front, and the
/// refresh button looks at once.
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
            Raise(nameof(Summary));
            Raise(nameof(Warning));
            if (!string.Equals(_workspace.GamePath, value.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                _workspace.GamePath = value.ImagePath;
                _workspace.Remember(_services.Settings, s => s with { GamePath = value.ImagePath });
            }
        }
    }

    public bool CanChoose => !_workspace.SessionActive;

    /// <summary>The card header: the chosen game's title, or that there is none.</summary>
    public string Summary => _selected switch
    {
        null => "Not chosen",
        { Running: true } game => game.Title,
        var game => game.Title + ", not running",
    };

    public string? Hint => _selected is null
        ? "Start the game once so it shows up here, then choose it. The app remembers it, so next time you can press Start first."
        : null;

    /// <summary>The hook cannot see an elevated game's input unless the mapper is elevated too (B10).</summary>
    public string? Warning => _selected is { } game ? Wording.RunAsAdministrator(game.Elevation, game.Title) : null;

    public Command Refresh { get; }

    /// <summary>No game is chosen, or the chosen one has not been seen running.</summary>
    private bool Waiting => _selected is not { Running: true };

    /// <summary>Looks again every <see cref="RescanMs"/> while waiting for the game.</summary>
    public void Tick(long nowMs)
    {
        if (Waiting && nowMs - _lastScan >= RescanMs)
        {
            Rescan();
        }
    }

    /// <summary>The window came back to the front, likely from starting the game. Looks again if still waiting for it.</summary>
    public void OnActivated()
    {
        if (Waiting)
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
        Raise(nameof(Summary));
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
