using ApexMapper.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels;

public sealed class GamePickerViewModel : ObservableViewModel, IDisposable
{
    private readonly IGameSelection _selection;
    private readonly Func<IReadOnlyList<GameProcess>> _enumerateGames;
    private readonly SynchronizationContext? _syncContext;
    private IReadOnlyList<GameProcess> _games = [];
    private GameProcess? _selectedGame;
    private string? _notice;
    private bool _refreshing;

    public GamePickerViewModel(IGameSelection selection,
        Func<IReadOnlyList<GameProcess>>? enumerateGames = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _enumerateGames = enumerateGames ?? GameSelection.EnumerateRunningGames;
        _syncContext = SynchronizationContext.Current;
        RefreshCommand = new RelayCommand(Refresh);
        _selection.Changed += OnSelectionChanged;
        Refresh();
    }

    public IReadOnlyList<GameProcess> Games => _games;
    public string? Notice => _notice;
    public IRelayCommand RefreshCommand { get; }

    public GameProcess? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (_refreshing) return;
            var selected = value is null ? null : _games.FirstOrDefault(value.IsSameProcess);
            if (value is not null && selected is null) return;
            _selection.Select(selected);
            SyncSelection();
        }
    }

    private void Refresh()
    {
        IReadOnlyList<GameProcess> games;
        string? notice;
        try
        {
            games = _enumerateGames()
                .Where(game => game.ProcessId > 0 && game.ProcessId != Environment.ProcessId
                    && game.ProcessStartTimeUtcTicks > 0 && !string.IsNullOrWhiteSpace(game.Title))
                .DistinctBy(game => (game.ProcessId, game.ProcessStartTimeUtcTicks))
                .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.ExecutableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.ProcessId).ToArray();
            notice = games.Count == 0 ? "Open your game, then refresh." : null;
        }
        catch (Exception)
        {
            games = [];
            notice = "Couldn't refresh games. Try again.";
        }

        var previous = _selection.SelectedGame;
        var selected = previous is null ? null : games.FirstOrDefault(previous.IsSameProcess);
        _refreshing = true;
        try
        {
            // WPF clears SelectedItem while ItemsSource changes. Keep that
            // transient value out of the process selection shared with mapping.
            SetProperty(ref _selectedGame, null, nameof(SelectedGame));
            SetProperty(ref _games, games, nameof(Games));
            SetProperty(ref _notice, notice, nameof(Notice));
            _selection.Select(selected);
            SyncSelection();
        }
        finally { _refreshing = false; }
    }

    private void OnSelectionChanged(object? sender, EventArgs change)
    {
        if (_syncContext is not null && _syncContext != SynchronizationContext.Current)
            _syncContext.Post(_ => SyncSelection(), null);
        else
            SyncSelection();
    }

    private void SyncSelection() => SetProperty(ref _selectedGame, _selection.SelectedGame, nameof(SelectedGame));
    public void Dispose() => _selection.Changed -= OnSelectionChanged;
}
