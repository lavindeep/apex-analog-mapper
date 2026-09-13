using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Profiles;

public sealed class BindingEditorViewModelTests
{
    [Theory]
    [InlineData(BindingTarget.ButtonA)]
    [InlineData(BindingTarget.LeftStickX)]
    public void Duplicate_outputs_cannot_silently_overwrite_an_existing_binding(BindingTarget target)
    {
        var editor = new BindingEditorViewModel(MakeProfile());
        editor.AddKeyCommand.Execute(null);
        editor.Rows[^1].FirstKey = new KeyId(0x12);
        editor.Rows[^1].Target = target;

        editor.TryBuildProfile(out var saved).Should().BeFalse();
        saved.Should().BeNull();
        editor.Error.Should().NotBeNullOrEmpty();

        editor.Rows[^1].Target = BindingTarget.ButtonB;
        editor.TryBuildProfile(out _).Should().BeTrue();
    }

    [Fact]
    public void Editing_and_removing_rows_leaves_original_profile_untouched()
    {
        var profile = MakeProfile();
        var originalSingle = profile.SingleBindings[0];
        var originalAxis = profile.AxisBindings[0];
        var editor = new BindingEditorViewModel(profile);

        editor.Rows[0].FirstKey = new KeyId(0x20);
        editor.Rows[0].Target = BindingTarget.ButtonB;
        editor.RemoveCommand.Execute(editor.Rows[1]);

        profile.SingleBindings.Should().ContainSingle().Which.Should().Be(originalSingle);
        profile.AxisBindings.Should().ContainSingle().Which.Should().Be(originalAxis);
        new BindingEditorViewModel(profile).Rows[0].FirstKey.Should().Be(originalSingle.Source);
    }

    [Fact]
    public void Saving_changes_only_keys_and_outputs_and_preserves_tuning_and_metadata()
    {
        var profile = MakeProfile();
        var editor = new BindingEditorViewModel(profile);
        editor.Rows[0].FirstKey = new KeyId(0x12);
        editor.Rows[0].Target = BindingTarget.ButtonB;
        editor.Rows[1].SecondKey = new KeyId(0x13);
        editor.Rows[1].Target = BindingTarget.RightStickY;

        editor.TryBuildProfile(out var saved).Should().BeTrue();

        saved.Should().BeEquivalentTo(profile with
        {
            SingleBindings = [profile.SingleBindings[0] with { Source = new KeyId(0x12), Target = BindingTarget.ButtonB }],
            AxisBindings = [profile.AxisBindings[0] with { PositiveKey = new KeyId(0x13), Target = BindingTarget.RightStickY }],
        });
        saved!.SingleBindings[0].Curve.Should().BeSameAs(profile.SingleBindings[0].Curve);
        saved.AxisBindings[0].Curve.Should().BeSameAs(profile.AxisBindings[0].Curve);
    }

    [Fact]
    public void Add_and_remove_build_expected_defaults_and_allow_one_key_on_multiple_outputs()
    {
        var editor = new BindingEditorViewModel(MakeProfile() with { SingleBindings = [], AxisBindings = [] });
        editor.AddKeyCommand.Execute(null);
        editor.Rows[0].FirstKey = new KeyId(0x39);
        editor.AddKeyCommand.Execute(null);
        editor.Rows[1].FirstKey = new KeyId(0x39);
        editor.Rows[1].Target = BindingTarget.ButtonB;
        editor.AddAxisCommand.Execute(null);
        editor.Rows[2].FirstKey = new KeyId(0x1E);
        editor.Rows[2].SecondKey = new KeyId(0x20);
        editor.AddKeyCommand.Execute(null);
        editor.RemoveCommand.Execute(editor.Rows[3]);

        editor.TryBuildProfile(out var saved).Should().BeTrue();

        saved!.SingleBindings.Should().Equal(
            new SingleKeyBinding(new KeyId(0x39), BindingTarget.ButtonA, LinearCurve.Instance, 0, 0),
            new SingleKeyBinding(new KeyId(0x39), BindingTarget.ButtonB, LinearCurve.Instance, 0, 0));
        saved.AxisBindings.Should().Equal(new AxisPairBinding(new KeyId(0x1E), new KeyId(0x20),
            BindingTarget.LeftStickX, LinearCurve.Instance, 80, 80, SocdMode.Neutral));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public void Incomplete_or_identical_axis_keys_block_save_and_can_be_corrected(
        bool axis, bool missingFirst, bool missingSecond)
    {
        var editor = new BindingEditorViewModel(MakeProfile() with { SingleBindings = [], AxisBindings = [] });
        (axis ? editor.AddAxisCommand : editor.AddKeyCommand).Execute(null);
        var row = editor.Rows[0];
        row.FirstKey = missingFirst ? null : new KeyId(0x1E);
        row.SecondKey = missingSecond ? null : new KeyId(0x1E);

        editor.TryBuildProfile(out var invalid).Should().BeFalse();
        invalid.Should().BeNull();
        editor.Error.Should().NotBeNullOrEmpty();

        row.FirstKey = new KeyId(0x1E);
        row.SecondKey = new KeyId(0x20);
        editor.TryBuildProfile(out _).Should().BeTrue();
        editor.Error.Should().BeNull();
    }

    private static Profile MakeProfile() => new("custom", "Custom profile",
        new DeviceMatcher(0x1038, 0x1234, "serial", null),
        new GameMatcher("game.exe", null, null), ActivationPolicy.Default,
        [new SingleKeyBinding(new KeyId(0x39), BindingTarget.ButtonA,
            new DeadzoneCurve(LinearCurve.Instance, 0.1f, 0.9f), 12, 34)],
        [new AxisPairBinding(new KeyId(0x1E), new KeyId(0x20), BindingTarget.LeftStickX,
            new DeadzoneCurve(LinearCurve.Instance, 0.2f, 0.8f), 56, 78, SocdMode.LastInputWins)],
        "Keep these notes");
}
