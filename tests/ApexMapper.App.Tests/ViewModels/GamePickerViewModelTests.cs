using System.ComponentModel;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels;
using FluentAssertions;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class GamePickerViewModelTests
{
    private static readonly GameProcess First = new(24001, 100, "Arena", "arena.exe");
    private static readonly GameProcess Second = new(24003, 200, "Racer", "racer.exe");

    [Fact]
    public void ListsRunningWindowsWithoutChoosingAnApplication()
    {
        var selection = new GameSelection(_ => 100);
        var rows = new List<GameProcess>
        {
            Second, First, First, First with { ProcessId = Environment.ProcessId, Title = "Mapper" },
        };
        using var picker = new GamePickerViewModel(selection, () => rows);

        picker.Games.Should().Equal(First, Second);
        picker.SelectedGame.Should().BeNull();
        selection.SelectedGame.Should().BeNull();
        picker.Notice.Should().BeNull();
        rows.Clear();
        picker.Games.Should().HaveCount(2);
    }

    [Fact]
    public void ExplicitChoiceUpdatesSharedSelection()
    {
        var selection = new GameSelection(_ => First.ProcessStartTimeUtcTicks);
        var changes = 0;
        selection.Changed += (_, _) => changes++;
        using var picker = new GamePickerViewModel(selection, () => [First]);

        picker.SelectedGame = Second;
        selection.SelectedGame.Should().BeNull();
        picker.SelectedGame = First;
        selection.SelectedGame.Should().Be(First);
        changes.Should().Be(1);
        selection.ValidateSelectedGame(out var validated).Should().BeNull();
        validated.Should().Be(First);
    }

    [Fact]
    public void RefreshPreservesProcessIdentityWhenOrderAndWindowTitleChange()
    {
        var selection = new GameSelection(_ => Second.ProcessStartTimeUtcTicks);
        IReadOnlyList<GameProcess> rows = [First, Second];
        using var picker = new GamePickerViewModel(selection, () => rows);
        picker.SelectedGame = Second;
        var changes = 0;
        selection.Changed += (_, _) => changes++;
        picker.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(picker.Games)) picker.SelectedGame = null;
        };
        var renamed = Second with { Title = "A race in progress" };
        rows = [renamed, First];

        picker.RefreshCommand.Execute(null);

        picker.SelectedGame.Should().Be(renamed);
        selection.SelectedGame.Should().Be(renamed);
        picker.Games.Should().Equal(renamed, First);
        changes.Should().Be(0, "refreshing metadata must not stop the same selected process");
    }

    [Fact]
    public void RefreshDoesNotFollowAReusedProcessId()
    {
        IReadOnlyList<GameProcess> rows = [First];
        var selection = new GameSelection(_ => First.ProcessStartTimeUtcTicks + 1);
        using var picker = new GamePickerViewModel(selection, () => rows);
        picker.SelectedGame = First;
        var changes = 0;
        selection.Changed += (_, _) => changes++;
        var restarted = First with { ProcessStartTimeUtcTicks = First.ProcessStartTimeUtcTicks + 1 };
        rows = [restarted];

        picker.RefreshCommand.Execute(null);

        picker.Games.Should().Equal(restarted);
        picker.SelectedGame.Should().BeNull();
        selection.SelectedGame.Should().BeNull();
        changes.Should().Be(1);
        picker.SelectedGame = restarted;
        selection.ValidateSelectedGame(out var validated).Should().BeNull();
        validated.Should().Be(restarted);
    }

    [Fact]
    public void MissingSelectedProcessClearsSelectionWithoutChoosingAnother()
    {
        IReadOnlyList<GameProcess> rows = [First, Second];
        var selection = new GameSelection(_ => null);
        using var picker = new GamePickerViewModel(selection, () => rows);
        picker.SelectedGame = First;
        rows = [Second];

        picker.RefreshCommand.Execute(null);

        picker.Games.Should().Equal(Second);
        picker.SelectedGame.Should().BeNull();
        selection.SelectedGame.Should().BeNull();
    }

    [Fact]
    public void EmptyListHasAUsefulRefreshPrompt()
    {
        using var picker = new GamePickerViewModel(new GameSelection(_ => null), () => []);

        picker.Games.Should().BeEmpty();
        picker.SelectedGame.Should().BeNull();
        picker.Notice.Should().Be("Open your game, then refresh.");
    }

    [Fact]
    public void FailedEnumerationClearsStaleChoicesAndCanRecover()
    {
        var fail = false;
        var selection = new GameSelection(_ => First.ProcessStartTimeUtcTicks);
        using var picker = new GamePickerViewModel(selection,
            () => fail ? throw new Win32Exception(5) : [First]);
        picker.SelectedGame = First;
        fail = true;

        picker.RefreshCommand.Execute(null);

        picker.Games.Should().BeEmpty();
        picker.SelectedGame.Should().BeNull();
        selection.SelectedGame.Should().BeNull();
        picker.Notice.Should().Be("Couldn't refresh games. Try again.");
        fail = false;
        picker.RefreshCommand.Execute(null);
        picker.Games.Should().Equal(First);
        picker.SelectedGame.Should().BeNull();
        picker.Notice.Should().BeNull();
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("restarted")]
    [InlineData("access denied")]
    public void StartValidationRejectsAnExpiredProcessBeforeRefresh(string failure)
    {
        var selection = new GameSelection(processId =>
        {
            processId.Should().Be(First.ProcessId);
            return failure switch
            {
                "exited" => null,
                "restarted" => First.ProcessStartTimeUtcTicks + 1,
                _ => throw new Win32Exception(5),
            };
        });
        using var picker = new GamePickerViewModel(selection, () => [First]);
        picker.SelectedGame = First;
        var changes = 0;
        selection.Changed += (_, _) => changes++;

        selection.ValidateSelectedGame(out var validated).Should().Be("Choose a running game before starting.");

        validated.Should().BeNull();
        selection.SelectedGame.Should().BeNull();
        picker.SelectedGame.Should().BeNull();
        changes.Should().Be(1);
    }

    [Fact]
    public void StartNeedsAnExplicitSelection()
    {
        var selection = new GameSelection(_ => throw new InvalidOperationException("No process should be queried."));

        selection.ValidateSelectedGame(out var validated).Should().Be("Choose a running game before starting.");

        validated.Should().BeNull();
    }

    [Fact]
    public void SelectionChangeDuringValidationCannotValidateOrClearTheWrongGame()
    {
        GameSelection? selection = null;
        selection = new GameSelection(_ =>
        {
            selection!.Select(Second);
            return First.ProcessStartTimeUtcTicks;
        });
        selection.Select(First);

        selection.ValidateSelectedGame(out var validated).Should().Be("Game changed. Start again.");

        validated.Should().BeNull();
        selection.SelectedGame.Should().Be(Second);
    }
}
