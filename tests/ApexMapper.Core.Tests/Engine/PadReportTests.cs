using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class PadReportTests
{
    [Theory]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32767)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 16384)]
    [InlineData(-0.5f, -16384)]
    [InlineData(2f, 32767)]
    [InlineData(-2f, -32767)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    public void Sticks_pack_symmetrically(float value, int expected)
    {
        Assert.Equal((short)expected, PadReport.PackStick(value));
    }

    [Theory]
    [InlineData(1f, 255)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 128)]
    [InlineData(-0.5f, 0)]
    [InlineData(2f, 255)]
    [InlineData(float.NaN, 0)]
    public void Triggers_pack_to_a_byte(float value, int expected)
    {
        Assert.Equal((byte)expected, PadReport.PackTrigger(value));
    }

    [Fact]
    public void Equality_is_by_value_so_sub_lsb_changes_do_not_count()
    {
        var a = PadReport.Neutral;
        var b = PadReport.Neutral;
        a.SetTrigger(PadTarget.RightTrigger, 0.5f);
        b.SetTrigger(PadTarget.RightTrigger, 0.5019f);
        Assert.Equal(a, b);
        b.SetTrigger(PadTarget.RightTrigger, 0.51f);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Buttons_set_and_clear_their_xinput_bits()
    {
        var report = PadReport.Neutral;
        report.SetButton(PadTarget.ButtonA, true);
        report.SetButton(PadTarget.LeftBumper, true);
        Assert.Equal(0x1100, report.Buttons);
        report.SetButton(PadTarget.ButtonA, false);
        Assert.Equal(0x0100, report.Buttons);
    }

    [Fact]
    public void Every_target_is_an_axis_a_trigger_or_a_button_with_a_bit()
    {
        foreach (var target in Enum.GetValues<PadTarget>())
        {
            var kinds = (target.IsAxis() ? 1 : 0) + (target.IsTrigger() ? 1 : 0) + (target.IsButton() ? 1 : 0);
            Assert.Equal(1, kinds);
            if (target.IsButton())
            {
                Assert.NotEqual(0, target.ButtonBit());
            }
        }
        Assert.Throws<ArgumentException>(() => PadTarget.ButtonA.ButtonBit() + PadTarget.LeftStickX.ButtonBit());
        var report = PadReport.Neutral;
        Assert.Throws<ArgumentException>(() => report.SetAxis(PadTarget.ButtonA, 1f));
        Assert.Throws<ArgumentException>(() => report.SetTrigger(PadTarget.LeftStickX, 1f));
    }
}
