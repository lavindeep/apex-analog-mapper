using ApexMapper.Core.Socd;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Socd;

public class SocdResolverTests
{
    [Fact]
    public void Single_side_returns_signed_value()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.Neutral, negative: 0.8f, positive: 0f, ref state).Should().Be(-0.8f);
        SocdResolver.Resolve(SocdMode.Neutral, negative: 0f, positive: 0.4f, ref state).Should().Be(0.4f);
    }

    [Fact]
    public void Neutral_zeros_when_both_active()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.Neutral, 0.7f, 0.6f, ref state).Should().Be(0f);
    }

    [Fact]
    public void Stronger_analog_wins_picks_larger_side()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.7f, 0.4f, ref state).Should().Be(-0.7f);
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.3f, 0.9f, ref state).Should().Be(0.9f);
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.5f, 0.5f, ref state).Should().Be(0f);
    }

    [Fact]
    public void Last_input_wins_remembers_the_most_recent_side()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0.8f, 0f, ref state);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0.8f, 0.6f, ref state).Should().Be(0.6f);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0f, 0.6f, ref state);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0.7f, 0.6f, ref state).Should().Be(-0.7f);
    }

    [Fact]
    public void Last_input_wins_falls_back_when_winner_releases()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0.5f, 0f, ref state);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0.5f, 0.5f, ref state).Should().BeApproximately(0.5f, 1e-6f);
        SocdResolver.Resolve(SocdMode.LastInputWins, 0.5f, 0f, ref state).Should().Be(-0.5f);
    }
}
