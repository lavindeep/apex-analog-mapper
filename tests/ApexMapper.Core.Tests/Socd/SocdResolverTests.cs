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
    }

    [Fact]
    public void Stronger_analog_wins_is_neutral_on_an_exact_tie_with_no_prior_winner()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.5f, 0.5f, ref state).Should().Be(0f);
    }

    [Fact]
    public void Stronger_analog_wins_holds_a_stable_winner_under_noise_around_equality()
    {
        var state = default(SocdState);
        // Positive wins decisively first.
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.4f, 0.7f, ref state).Should().Be(0.7f);

        // Now both sides hover around 0.5, jittering by +/- 5e-3 (well inside the band).
        // The winner must not flap to the other side or collapse to neutral.
        for (var i = 0; i < 10; i++)
        {
            var neg = 0.5f + (i % 2 == 0 ? 0.005f : -0.005f);
            var pos = 0.5f - (i % 2 == 0 ? 0.005f : -0.005f);
            var result = SocdResolver.Resolve(SocdMode.StrongerAnalogWins, neg, pos, ref state);
            result.Should().BeGreaterThan(0f, "positive must keep winning through sub-band noise");
        }
    }

    [Fact]
    public void Stronger_analog_wins_switches_on_a_genuine_crossover()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.4f, 0.7f, ref state).Should().Be(0.7f);
        // Negative now exceeds positive by more than the band: the winner switches.
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.7f, 0.4f, ref state).Should().Be(-0.7f);
    }

    [Fact]
    public void Stronger_analog_wins_single_side_is_unaffected_by_hysteresis()
    {
        var state = default(SocdState);
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.4f, 0.7f, ref state).Should().Be(0.7f);
        // Positive releases; only negative is active — it drives the axis regardless of the
        // remembered winner.
        SocdResolver.Resolve(SocdMode.StrongerAnalogWins, 0.6f, 0f, ref state).Should().Be(-0.6f);
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
