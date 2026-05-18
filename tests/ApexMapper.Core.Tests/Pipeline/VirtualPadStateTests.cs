using ApexMapper.Core.Pipeline;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Pipeline;

public class VirtualPadStateTests
{
    [Fact]
    public void Reset_zeroes_all_fields()
    {
        var pad = new VirtualPadState
        {
            LeftStickX = 0.5f,
            RightTrigger = 0.7f,
            ButtonA = true,
        };
        pad.Reset();
        pad.LeftStickX.Should().Be(0f);
        pad.RightTrigger.Should().Be(0f);
        pad.ButtonA.Should().BeFalse();
    }

    [Fact]
    public void Targets_cover_all_xinput_controls()
    {
        Enum.GetNames(typeof(BindingTarget)).Should().Contain(new[]
        {
            "LeftStickX", "LeftStickY", "RightStickX", "RightStickY",
            "LeftTrigger", "RightTrigger",
            "ButtonA", "ButtonB", "ButtonX", "ButtonY",
            "ButtonLB", "ButtonRB", "ButtonStart", "ButtonBack",
            "ButtonLS", "ButtonRS", "ButtonGuide",
            "DpadUp", "DpadDown", "DpadLeft", "DpadRight",
        });
    }
}
