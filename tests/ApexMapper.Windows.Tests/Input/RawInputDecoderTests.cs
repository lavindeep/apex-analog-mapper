using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

public class RawInputDecoderTests
{
    [Theory]
    [InlineData(0x11, 0, 0x11, true)]
    [InlineData(0x11, User32.RI_KEY_BREAK, 0x11, false)]
    [InlineData(0x48, User32.RI_KEY_E0, 0xE048, true)]
    [InlineData(0x4B, User32.RI_KEY_E0 | User32.RI_KEY_BREAK, 0xE04B, false)]
    [InlineData(0x1D, User32.RI_KEY_E1, 0xE11D, true)]
    [InlineData(0x1D, User32.RI_KEY_E0, 0xE01D, true)]
    [InlineData(0x5B, User32.RI_KEY_E0, 0xE05B, true)]
    public void Make_codes_and_prefix_flags_decode_to_scan_codes(int make, int flags, int expected, bool down)
    {
        Assert.True(RawInputDecoder.TryDecode((ushort)make, (ushort)flags, out var code, out var isDown));

        Assert.Equal(expected, code.Value);
        Assert.Equal(down, isDown);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0xFF, 0)]
    [InlineData(0x100, 0)]
    [InlineData(0x2A, User32.RI_KEY_E0)]
    [InlineData(0x36, User32.RI_KEY_E0 | User32.RI_KEY_BREAK)]
    public void Overrun_zero_and_fake_shift_events_are_dropped(int make, int flags)
    {
        Assert.False(RawInputDecoder.TryDecode((ushort)make, (ushort)flags, out _, out _));
    }

    [Fact]
    public void Plain_shift_is_kept()
    {
        Assert.True(RawInputDecoder.TryDecode(0x2A, 0, out var code, out _));
        Assert.Equal(0x2A, code.Value);
    }
}
