using ApexMapper.Core.Keys;
using Xunit;

namespace ApexMapper.Core.Tests.Keys;

public class ScanCodeTests
{
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0xE000)]
    [InlineData(0x0100)]
    [InlineData(0xE200)]
    [InlineData(0x1234)]
    public void Rejects_codes_outside_the_three_pages(int value)
    {
        Assert.False(ScanCode.IsValid((ushort)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanCode((ushort)value));
    }

    [Fact]
    public void Extended_keys_get_distinct_slots_from_their_plain_twins()
    {
        var up = new ScanCode(0xE048);
        var numpad8 = new ScanCode(0x48);
        var pause = new ScanCode(0xE11D);
        var leftCtrl = new ScanCode(0x1D);

        Assert.NotEqual(up.Slot, numpad8.Slot);
        Assert.NotEqual(pause.Slot, leftCtrl.Slot);
        Assert.Equal(0x48, numpad8.Slot);
        Assert.Equal(256 + 0x48, up.Slot);
        Assert.Equal(512 + 0x1D, pause.Slot);
    }

    [Fact]
    public void Every_valid_code_round_trips_through_its_slot()
    {
        for (var value = 0; value <= 0xFFFF; value++)
        {
            if (!ScanCode.IsValid((ushort)value))
            {
                continue;
            }
            var code = new ScanCode((ushort)value);
            Assert.InRange(code.Slot, 1, ScanCode.SlotCount - 1);
            Assert.Equal(code, ScanCode.FromSlot(code.Slot));
        }
    }

    [Theory]
    [InlineData(0x1D, true)]
    [InlineData(0xE01D, true)]
    [InlineData(0x38, true)]
    [InlineData(0xE038, true)]
    [InlineData(0xE05B, true)]
    [InlineData(0xE05C, true)]
    [InlineData(0x58, true)]
    [InlineData(0x11, false)]
    [InlineData(0x2A, false)]
    [InlineData(0x57, false)]
    public void Reserved_keys_are_ctrl_alt_win_and_f12(int value, bool reserved)
    {
        Assert.Equal(reserved, new ScanCode((ushort)value).IsReserved);
    }
}
