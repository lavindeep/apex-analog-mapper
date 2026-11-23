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
        Assert.Equal("ForzaHorizon6, not running", game.Summary);
        Assert.Equal(AppHarness.Game, _h.Workspace.GamePath);
        Assert.Equal(2, game.Games.Count);
        Assert.Null(game.Hint);
    }

    [Fact]
    public void Only_a_game_in_a_steam_library_gets_the_steam_input_step()
    {
        const string Steam = @"C:\Program Files (x86)\Steam\steamapps\common\ForzaHorizon6\ForzaHorizon6.exe";
        _h.Windows = [new GameWindow("Forza Horizon 6", Steam, Elevation.Visible)];

        Assert.Null(Create(AppHarness.Game).Hint);
        Assert.Equal("This game runs through Steam. Turn off Steam Input for it in Steam, or Steam may hide or remap the controller.", Create(Steam).Hint);
    }

    [Fact]
    public void While_mapping_the_game_row_says_how_to_choose_another()
    {
        var game = Create(AppHarness.Game);
        Assert.Equal("Mapping works while this game is in front", game.RowText);

        _h.Workspace.Session = SessionState.Running;
        Assert.False(game.CanChoose);
        Assert.Equal("Stop mapping to choose a different game", game.RowText);
        Assert.False(game.Refresh.CanExecute(null));
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
        Assert.Null(game.Hint);
        Assert.Equal("Launch your game, then choose its window from the list", game.RowText);

        game.Selected = game.Games[0];

        Assert.Equal(AppHarness.Game, _h.Workspace.GamePath);
        Assert.Equal(AppHarness.Game, _h.SavedSettings.GamePath);
        Assert.Equal("Forza Horizon 6", game.Summary);
        Assert.StartsWith("Because Forza Horizon 6 runs as administrator and this app does not, this app leaves it alone.", game.Warning);

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
    public void The_list_is_scanned_again_until_the_remembered_game_runs_and_whenever_the_window_comes_back()
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

        // Closed while this window was behind it.
        _h.Windows = [];
        game.OnActivated();
        Assert.Equal(4, _h.WindowScans);
        Assert.Equal(AppHarness.Game, game.Selected?.ImagePath);
        Assert.False(game.Selected?.Running);
    }

    [Fact]
    public void With_no_game_chosen_the_list_is_scanned_again_while_it_is_closed_and_when_the_window_comes_back()
    {
        var game = Create(null);

        // Not while the list is open, where a new list would move the items under the pointer.
        game.IsListOpen = true;
        game.Tick(_h.Now + GameViewModel.RescanMs);
        Assert.Equal(1, _h.WindowScans);

        game.IsListOpen = false;
        game.Tick(_h.Now + GameViewModel.RescanMs);
        Assert.Equal(2, _h.WindowScans);

        _h.Windows = [new GameWindow("Forza Horizon 6", AppHarness.Game, Elevation.Visible)];
        game.OnActivated();
        Assert.Equal(3, _h.WindowScans);
        Assert.Single(game.Games);
    }
}
