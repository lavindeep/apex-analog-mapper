using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Keys;

namespace ApexMapper.Input.Abstractions.Tests.Keys;

public class ScanCodeEncoderTests
{
    [Fact]
    public void Encode_no_prefix_places_scancode_in_low_byte()
    {
        ScanCodeEncoder.Encode(prefix: 0x00, baseScanCode: 0x1E)
            .Should().Be(KeyId.FromScanCode(0x001E));
    }

    [Fact]
    public void Encode_E0_prefix_places_E0_in_high_byte()
    {
        ScanCodeEncoder.Encode(prefix: 0xE0, baseScanCode: 0x4D)
            .Should().Be(KeyId.FromScanCode(0xE04D));
    }

    [Fact]
    public void Encode_E0_prefix_for_numpad_enter()
    {
        ScanCodeEncoder.Encode(prefix: 0xE0, baseScanCode: 0x1C)
            .Should().Be(KeyId.FromScanCode(0xE01C));
    }

    [Fact]
    public void Encode_E0_prefix_for_right_ctrl()
    {
        ScanCodeEncoder.Encode(prefix: 0xE0, baseScanCode: 0x1D)
            .Should().Be(KeyId.FromScanCode(0xE01D));
    }

    [Fact]
    public void Encode_E1_prefix_for_pause()
    {
        ScanCodeEncoder.Encode(prefix: 0xE1, baseScanCode: 0x1D)
            .Should().Be(KeyId.FromScanCode(0xE11D));
    }

    [Fact]
    public void Encode_invalid_prefix_throws()
    {
        var act = () => ScanCodeEncoder.Encode(prefix: 0x42, baseScanCode: 0x1E);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryDecode_extended_E0_key_unpacks_prefix_and_base()
    {
        var ok = ScanCodeEncoder.TryDecode(KeyId.FromScanCode(0xE04D), out var prefix, out var baseScanCode);
        ok.Should().BeTrue();
        prefix.Should().Be(0xE0);
        baseScanCode.Should().Be(0x4D);
    }

    [Fact]
    public void TryDecode_standard_key_returns_zero_prefix()
    {
        var ok = ScanCodeEncoder.TryDecode(KeyId.FromScanCode(0x001E), out var prefix, out var baseScanCode);
        ok.Should().BeTrue();
        prefix.Should().Be(0x00);
        baseScanCode.Should().Be(0x1E);
    }

    [Fact]
    public void TryDecode_E1_pause_unpacks_prefix_and_base()
    {
        var ok = ScanCodeEncoder.TryDecode(KeyId.FromScanCode(0xE11D), out var prefix, out var baseScanCode);
        ok.Should().BeTrue();
        prefix.Should().Be(0xE1);
        baseScanCode.Should().Be(0x1D);
    }

    [Fact]
    public void TryDecode_unknown_prefix_returns_false()
    {
        var ok = ScanCodeEncoder.TryDecode(KeyId.FromScanCode(0x4200), out var prefix, out var baseScanCode);
        ok.Should().BeFalse();
        prefix.Should().Be(0x00);
        baseScanCode.Should().Be(0x00);
    }

    [Theory]
    [InlineData((byte)0x00, (byte)0x1E)]
    [InlineData((byte)0x00, (byte)0x39)]
    [InlineData((byte)0xE0, (byte)0x4D)]
    [InlineData((byte)0xE0, (byte)0x1C)]
    [InlineData((byte)0xE0, (byte)0x1D)]
    [InlineData((byte)0xE0, (byte)0x5B)]
    [InlineData((byte)0xE1, (byte)0x1D)]
    public void Encode_then_TryDecode_round_trips(byte prefix, byte baseScanCode)
    {
        var id = ScanCodeEncoder.Encode(prefix, baseScanCode);
        var ok = ScanCodeEncoder.TryDecode(id, out var decodedPrefix, out var decodedBase);
        ok.Should().BeTrue();
        decodedPrefix.Should().Be(prefix);
        decodedBase.Should().Be(baseScanCode);
    }
}
