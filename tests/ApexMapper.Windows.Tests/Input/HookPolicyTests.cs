using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

/// <summary>Every rule of the design's blocking section, B1 to B7 in the requirements.</summary>
public class HookPolicyTests
{
    private const int W = 0x11;
    private const int S = 0x1F;
    private const int Q = 0x10;
    private const int LeftCtrl = 0x1D;
    private const int LeftAlt = 0x38;
    private const int RightAlt = 256 + 0x38;
    private const int LeftWin = 256 + 0x5B;
    private const int F12 = 0x58;

    private static HookPolicy Policy()
    {
        var policy = new HookPolicy();
        policy.SetMapped([new ScanCode(W), new ScanCode(S)]);
        return policy;
    }

    [Fact]
    public void B1_a_mapped_key_is_swallowed_only_while_the_game_is_foreground()
    {
        var policy = Policy();

        Assert.True(policy.Decide(W, true, false, true));
        Assert.True(policy.Decide(W, false, false, true));
        Assert.False(policy.Decide(W, true, false, false));
        Assert.False(policy.Decide(W, false, false, false));
        Assert.Equal(2, policy.SwallowedCount);
    }

    [Fact]
    public void An_unmapped_key_is_never_swallowed()
    {
        var policy = Policy();

        Assert.False(policy.Decide(Q, true, false, true));
        Assert.False(policy.Decide(Q, false, false, true));
    }

    [Fact]
    public void B2_injected_events_pass_unless_test_mode_is_on()
    {
        var policy = Policy();

        Assert.False(policy.Decide(W, true, true, true));
        Assert.False(policy.Decide(W, false, true, true));
        Assert.False(policy.IsSwallowedDown(W));

        policy.SwallowInjected = true;
        Assert.True(policy.Decide(W, true, true, true));
        Assert.True(policy.Decide(W, false, true, true));
    }

    [Fact]
    public void B3_a_key_up_is_swallowed_only_if_its_down_was()
    {
        var policy = Policy();

        Assert.False(policy.Decide(W, true, false, false));
        Assert.False(policy.Decide(W, false, false, true));

        Assert.True(policy.Decide(W, true, false, true));
        Assert.True(policy.Decide(W, false, false, true));
    }

    [Fact]
    public void Auto_repeat_follows_the_first_down()
    {
        var policy = Policy();

        Assert.True(policy.Decide(W, true, false, true));
        Assert.True(policy.Decide(W, true, false, true));
        Assert.True(policy.Decide(W, true, false, false));
        Assert.Equal(3, policy.SwallowedCount);

        Assert.False(policy.Decide(S, true, false, false));
        Assert.False(policy.Decide(S, true, false, true));
    }

    [Fact]
    public void B4_a_key_down_at_install_passes_until_released()
    {
        var policy = Policy();
        policy.MarkDown(W);

        Assert.False(policy.Decide(W, true, false, true));
        Assert.False(policy.Decide(W, false, false, true));
        Assert.True(policy.Decide(W, true, false, true));
    }

    [Fact]
    public void B5_a_modifier_chord_passes_through_and_releases_held_mapped_keys()
    {
        var policy = Policy();
        Assert.True(policy.Decide(W, true, false, true));

        Assert.False(policy.Decide(LeftAlt, true, false, true));
        Assert.True(policy.AltDown);
        Assert.False(policy.IsSwallowedDown(W));
        Assert.False(policy.Decide(W, false, false, true));

        Assert.False(policy.Decide(S, true, false, true));
        Assert.False(policy.Decide(S, false, false, true));

        Assert.False(policy.Decide(LeftAlt, false, false, true));
        Assert.False(policy.AltDown);
        Assert.True(policy.Decide(S, true, false, true));
    }

    [Theory]
    [InlineData(LeftCtrl)]
    [InlineData(RightAlt)]
    [InlineData(LeftWin)]
    public void Every_reserved_modifier_counts(int modifier)
    {
        var policy = Policy();

        Assert.False(policy.Decide(modifier, true, false, true));
        Assert.False(policy.Decide(W, true, false, true));
        Assert.False(policy.Decide(modifier, false, false, true));
        Assert.False(policy.Decide(W, false, false, true));
        Assert.True(policy.Decide(W, true, false, true));
    }

    [Fact]
    public void B7_foreground_loss_hands_held_keys_back_without_injecting()
    {
        var policy = Policy();
        Assert.True(policy.Decide(W, true, false, true));

        policy.ForegroundLost();

        Assert.False(policy.IsSwallowedDown(W));
        Assert.False(policy.Decide(W, true, false, false));
        Assert.False(policy.Decide(W, false, false, false));
    }

    [Fact]
    public void A_key_up_after_the_flag_dropped_passes_even_before_the_tracker_edge()
    {
        var policy = Policy();
        Assert.True(policy.Decide(W, true, false, true));

        Assert.False(policy.Decide(W, false, false, false));
        Assert.True(policy.Decide(W, true, false, true));
    }

    [Fact]
    public void The_stop_chord_is_ctrl_alt_f12_from_observed_modifiers()
    {
        var policy = Policy();

        Assert.False(policy.IsStopChord(F12, true));
        policy.Decide(LeftCtrl, true, false, true);
        Assert.False(policy.IsStopChord(F12, true));
        policy.Decide(LeftAlt, true, false, true);
        Assert.True(policy.IsStopChord(F12, true));
        Assert.False(policy.IsStopChord(F12, false));
        Assert.False(policy.IsStopChord(W, true));
    }

    [Fact]
    public void Reset_forgets_held_keys_and_modifiers()
    {
        var policy = Policy();
        policy.Decide(LeftCtrl, true, false, true);
        policy.Decide(W, true, false, true);

        policy.Reset();

        Assert.False(policy.CtrlDown);
        Assert.False(policy.IsSwallowedDown(W));
        Assert.True(policy.Decide(W, true, false, true));
    }
}
