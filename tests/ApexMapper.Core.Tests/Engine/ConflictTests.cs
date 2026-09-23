using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class ConflictTests
{
    [Theory]
    [InlineData(ConflictRule.LastInputWins)]
    [InlineData(ConflictRule.Neutral)]
    public void One_side_held_drives_the_axis_with_its_sign(ConflictRule rule)
    {
        var state = new ConflictState();
        Assert.Equal(-0.6f, state.Resolve(rule, 0.6f, 0f));
        Assert.Equal(0.3f, state.Resolve(rule, 0f, 0.3f));
        Assert.Equal(0f, state.Resolve(rule, 0f, 0f));
    }

    [Fact]
    public void Last_input_wins_then_hands_over_on_release()
    {
        var state = new ConflictState();
        state.Resolve(ConflictRule.LastInputWins, 0.8f, 0f);
        Assert.Equal(0.5f, state.Resolve(ConflictRule.LastInputWins, 0.8f, 0.5f));
        Assert.Equal(-0.8f, state.Resolve(ConflictRule.LastInputWins, 0.8f, 0f));
        // Re-press the other side: it wins again.
        Assert.Equal(0.2f, state.Resolve(ConflictRule.LastInputWins, 0.8f, 0.2f));
    }

    [Fact]
    public void Neutral_centres_while_both_are_held()
    {
        var state = new ConflictState();
        state.Resolve(ConflictRule.Neutral, 0.8f, 0f);
        Assert.Equal(0f, state.Resolve(ConflictRule.Neutral, 0.8f, 0.5f));
        Assert.Equal(0.5f, state.Resolve(ConflictRule.Neutral, 0f, 0.5f));
    }

    [Fact]
    public void Both_pressed_in_the_same_tick_favours_positive_and_reset_forgets_history()
    {
        var state = new ConflictState();
        Assert.Equal(0.4f, state.Resolve(ConflictRule.LastInputWins, 0.9f, 0.4f));
        state.Reset();
        Assert.Equal(0.4f, state.Resolve(ConflictRule.LastInputWins, 0.9f, 0.4f));
    }
}
