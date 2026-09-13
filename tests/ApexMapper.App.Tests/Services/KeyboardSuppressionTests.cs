using ApexMapper.App.Services;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Pipeline;

namespace ApexMapper.App.Tests.Services;

public sealed class KeyboardSuppressionTests
{
    private static readonly KeyId W = new(0x11);
    private static readonly KeyId Shift = new(0x2A);
    private static readonly KeyId Up = new(0xE048);

    [Fact]
    public void Initially_held_key_passes_repeats_and_release_then_new_press_is_suppressed()
    {
        var policy = new KeyboardSuppressionPolicy(new[] { W });
        policy.SetInitiallyHeld(W);
        policy.SetActive(true);
        Assert.False(policy.ShouldSuppress(W, true, false, false));
        Assert.False(policy.ShouldSuppress(W, false, false, false));
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        Assert.True(policy.ShouldSuppress(W, false, false, false));
    }

    [Fact]
    public void Focus_loss_or_stop_passes_held_keys_until_release()
    {
        var policy = new KeyboardSuppressionPolicy(new[] { W });
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        policy.SetActive(false);
        Assert.False(policy.ShouldSuppress(W, true, false, false));
        policy.SetActive(true);
        Assert.False(policy.ShouldSuppress(W, true, false, false));
        Assert.False(policy.ShouldSuppress(W, false, false, false));
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        policy.SetActive(false);
        Assert.False(policy.ShouldSuppress(W, false, false, false));
    }

    [Fact]
    public void Activation_does_not_take_over_a_down_that_was_passed()
    {
        var policy = new KeyboardSuppressionPolicy(new[] { W });
        Assert.False(policy.ShouldSuppress(W, true, false, false));
        policy.SetActive(true);
        Assert.False(policy.ShouldSuppress(W, false, false, false));
        Assert.True(policy.ShouldSuppress(W, true, false, false));
    }

    [Theory]
    [InlineData(0x1D)]
    [InlineData(0xE01D)]
    [InlineData(0x38)]
    [InlineData(0xE038)]
    [InlineData(0xE05B)]
    [InlineData(0xE05C)]
    public void Shortcut_modifier_and_chord_are_never_suppressed(int code)
    {
        var modifier = new KeyId((ushort)code);
        var policy = new KeyboardSuppressionPolicy(new[] { W, modifier });
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        Assert.False(policy.ShouldSuppress(modifier, true, false, false));
        Assert.False(policy.ShouldSuppress(W, true, false, true));
        Assert.False(policy.ShouldSuppress(modifier, false, false, true));
        Assert.False(policy.ShouldSuppress(W, false, false, false));
        Assert.True(policy.ShouldSuppress(W, true, false, false));
    }

    [Fact]
    public void Mapped_shift_supports_clutch_while_emergency_key_passes()
    {
        var f12 = new KeyId(0x58);
        var policy = new KeyboardSuppressionPolicy(new[] { Shift, f12 });
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(Shift, true, false, false));
        Assert.True(policy.ShouldSuppress(Shift, false, false, false));
        Assert.False(policy.ShouldSuppress(f12, true, false, false));
        Assert.False(policy.ShouldSuppress(f12, false, false, false));
        Assert.False(policy.ShouldSuppress(f12, true, false, true));
    }

    [Fact]
    public void Injected_events_pass_without_altering_physical_key_pairing()
    {
        var policy = new KeyboardSuppressionPolicy(new[] { W });
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        Assert.False(policy.ShouldSuppress(W, false, true, false));
        Assert.True(policy.ShouldSuppress(W, false, false, false));
        Assert.False(policy.ShouldSuppress(W, true, true, false));
        Assert.False(policy.ShouldSuppress(W, false, false, false));
    }

    [Fact]
    public void Extended_scan_codes_do_not_match_unextended_keys()
    {
        var policy = new KeyboardSuppressionPolicy(new[] { Up });
        policy.SetActive(true);
        Assert.False(policy.ShouldSuppress(new(0x48), true, false, false));
        Assert.True(policy.ShouldSuppress(Up, true, false, false));
        Assert.True(policy.ShouldSuppress(Up, false, false, false));
        Assert.False(policy.ShouldSuppress(new(0x48), false, false, false));
    }

    [Fact]
    public void A_new_lease_policy_does_not_reuse_old_swallowed_downs()
    {
        var oldPolicy = new KeyboardSuppressionPolicy(new[] { W });
        oldPolicy.SetActive(true);
        Assert.True(oldPolicy.ShouldSuppress(W, true, false, false));
        oldPolicy.SetActive(false);
        var newPolicy = new KeyboardSuppressionPolicy(new[] { W });
        newPolicy.SetInitiallyHeld(W);
        newPolicy.SetActive(true);
        Assert.False(newPolicy.ShouldSuppress(W, false, false, false));
        Assert.True(newPolicy.ShouldSuppress(W, true, false, false));
    }

    [Fact]
    public void Captured_space_drives_button_and_releases_without_overwriting_analog_depth()
    {
        var space = new KeyId(0x39);
        var policy = new KeyboardSuppressionPolicy(new[] { W, space }, new[] { space });
        var store = new KeyStateStore(new KeyIndex(new[] { W, space }));
        var pipeline = new BindingPipeline(new[]
        {
            new SingleKeyBinding(space, BindingTarget.ButtonA, LinearCurve.Instance, 0, 0),
        }, Array.Empty<AxisPairBinding>());
        var pad = default(VirtualPadState);
        store.Set(W, 0.4f, KeyProvenance.Analog);
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(W, true, false, false));
        Assert.True(policy.ShouldSuppress(space, true, false, false));
        policy.ApplyTo(store, true);
        pipeline.Tick(store, 1, ref pad);
        Assert.True(pad.ButtonA);
        Assert.Equal(new KeyState(0.4f, KeyProvenance.Analog), store.Get(W));

        store.Set(space, 0, KeyProvenance.Digital);
        policy.ApplyTo(store, true);
        Assert.Equal(1f, store.Get(space).Value);
        Assert.True(policy.ShouldSuppress(space, false, false, false));
        policy.ApplyTo(store, true);
        pipeline.Tick(store, 1, ref pad);
        Assert.False(pad.ButtonA);
    }

    [Fact]
    public void Mapped_shift_with_Q_and_E_captures_independent_button_states()
    {
        var q = new KeyId(0x10);
        var e = new KeyId(0x12);
        var keys = new[] { Shift, q, e };
        var policy = new KeyboardSuppressionPolicy(keys, keys);
        var store = new KeyStateStore(new KeyIndex(keys));
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(Shift, true, false, false));
        foreach (var gear in new[] { q, e })
        {
            Assert.True(policy.ShouldSuppress(gear, true, false, false));
            policy.ApplyTo(store, true);
            Assert.Equal(1f, store.Get(Shift).Value);
            Assert.Equal(1f, store.Get(gear).Value);
            Assert.True(policy.ShouldSuppress(gear, false, false, false));
            policy.ApplyTo(store, true);
            Assert.Equal(1f, store.Get(Shift).Value);
            Assert.Equal(0f, store.Get(gear).Value);
        }
        Assert.True(policy.ShouldSuppress(Shift, false, false, false));
        policy.ApplyTo(store, true);
        Assert.Equal(0f, store.Get(Shift).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Focus_loss_or_shortcut_clears_capture_until_fresh_press(bool shortcut)
    {
        var policy = new KeyboardSuppressionPolicy(new[] { Shift }, new[] { Shift });
        var store = new KeyStateStore(new KeyIndex(new[] { Shift }));
        policy.SetActive(true);
        Assert.True(policy.ShouldSuppress(Shift, true, false, false));
        policy.ApplyTo(store, true);
        Assert.Equal(1f, store.Get(Shift).Value);
        if (shortcut) policy.ShouldSuppress(new(0x1D), true, false, false);
        else policy.SetActive(false);
        policy.SetActive(true);
        policy.ApplyTo(store, true);
        Assert.Equal(0f, store.Get(Shift).Value);
        Assert.False(policy.ShouldSuppress(Shift, true, false, false));
        Assert.False(policy.ShouldSuppress(Shift, false, false, false));
        Assert.True(policy.ShouldSuppress(Shift, true, false, false));
        policy.ApplyTo(store, false);
        Assert.Equal(0f, store.Get(Shift).Value);
    }
}
