using ApexMapper.App.ViewModels;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class GameViewModelTests : IDisposable
{
    private const string Notepad = @"C:\Windows\notepad.exe";

    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private GameViewModel Create(string? remembered)
    {
        var game = new GameViewModel(_h.Services, _h.Workspace, remembered);
        game.Rescan();
        return game;
    }

    [Fact]
    public void The_remembered_game_stays_chosen_while_it_is_not_running()
    {
        _h.Windows = [new GameWindow("Untitled - Notepad", Notepad, Elevation.Visible)];

        var game = Create(AppHarness.Game);

        Assert.Equal(AppHarness.Game, game.Selected?.ImagePath);
        Assert.Equal("ForzaHorizon6.exe, not running", game.Selected?.Detail);
        Assert.Equal(AppHarness.Game, _h.Workspace.GamePath);
        Assert.Equal(2, game.Games.Count);
        Assert.Null(game.Hint);
    }

    [Fact]
    public void A_chosen_game_is_remembered_and_one_run_as_administrator_warns()
    {
        _h.Windows =
        [
            new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Elevated),
            new GameWindow("Untitled - Notepad", Notepad, Elevation.Visible),
        ];
        var game = Create(null);
        Assert.Null(game.Selected);
        Assert.NotNull(game.Hint);

        game.Selected = game.Games[0];

        Assert.Equal(AppHarness.Game, _h.Workspace.GamePath);
        Assert.Equal(AppHarness.Game, _h.SavedSettings.GamePath);
        Assert.Contains("runs as administrator", game.Warning);

        game.Selected = game.Games[1];
        Assert.Null(game.Warning);
    }

    [Fact]
    public void The_game_cannot_change_while_a_session_runs()
    {
        _h.Windows = [new GameWindow("Untitled - Notepad", Notepad, Elevation.Visible)];
        var game = Create(AppHarness.Game);
        _h.Workspace.Session = SessionState.Running;

        game.Selected = game.Games.Single(g => g.ImagePath == Notepad);

        Assert.Equal(AppHarness.Game, _h.Workspace.GamePath);
        Assert.False(game.CanChoose);
    }

    [Fact]
    public void The_list_is_scanned_again_until_the_remembered_game_runs()
    {
        var game = Create(AppHarness.Game);
        Assert.Equal(1, _h.WindowScans);

        game.Tick(_h.Now + GameViewModel.RescanMs - 1);
        Assert.Equal(1, _h.WindowScans);

        _h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
        game.Tick(_h.Now + GameViewModel.RescanMs);
        Assert.Equal(2, _h.WindowScans);
        Assert.True(game.Selected?.Running);

        game.Tick(_h.Now + 10 * GameViewModel.RescanMs);
        Assert.Equal(2, _h.WindowScans);

        game.Refresh.Execute(null);
        Assert.Equal(3, _h.WindowScans);
        Assert.Equal(AppHarness.Game, game.Selected?.ImagePath);
    }
}
