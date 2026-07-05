using System.Buffers.Binary;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.RawInput;

namespace ApexMapper.Input.Abstractions.Tests.RawInput;

public class RawInputMessageDecoderTests
{
    private const ushort RI_KEY_BREAK = 0x1;
    private const ushort RI_KEY_E0    = 0x2;
    private const ushort RI_KEY_E1    = 0x4;

    private static byte[] RawKeyboard(ushort makeCode, ushort flags)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), makeCode);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), flags);
        return bytes;
    }

    [Fact]
    public void Normal_key_down_decodes_scancode_and_isdown_true()
    {
        var buffer = RawKeyboard(makeCode: 0x1E, flags: 0);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
        ev.IsDown.Should().BeTrue();
    }

    [Fact]
    public void Normal_key_up_decodes_isdown_false()
    {
        var buffer = RawKeyboard(makeCode: 0x1E, flags: RI_KEY_BREAK);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
        ev.IsDown.Should().BeFalse();
    }

    [Fact]
    public void E0_extended_key_down_produces_E0_prefixed_scancode()
    {
        var buffer = RawKeyboard(makeCode: 0x4D, flags: RI_KEY_E0);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0xE04D);
        ev.IsDown.Should().BeTrue();
    }

    [Fact]
    public void E0_extended_key_up_produces_E0_prefix_and_isdown_false()
    {
        var buffer = RawKeyboard(makeCode: 0x4D, flags: (ushort)(RI_KEY_E0 | RI_KEY_BREAK));

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0xE04D);
        ev.IsDown.Should().BeFalse();
    }

    [Fact]
    public void E1_pause_key_produces_E1_prefixed_scancode()
    {
        var buffer = RawKeyboard(makeCode: 0x1D, flags: RI_KEY_E1);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0xE11D);
        ev.IsDown.Should().BeTrue();
    }

    [Fact]
    public void Fake_shift_makecode_0xFF_is_filtered_out()
    {
        var buffer = RawKeyboard(makeCode: 0xFF, flags: 0);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Zero_scancode_is_filtered_out()
    {
        var buffer = RawKeyboard(makeCode: 0x0000, flags: 0);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Timestamp_and_device_id_flow_through_to_event()
    {
        var buffer = RawKeyboard(makeCode: 0x1E, flags: 0);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0x4_0000, timestampTicks: 12345L, out var ev);

        ok.Should().BeTrue();
        ev.TimestampTicks.Should().Be(12345L);
        ev.DeviceId.Should().Be(0x4_0000);
    }

    [Fact]
    public void Short_buffer_under_four_bytes_returns_false()
    {
        var shortBuffer = new byte[3];

        var ok = RawInputMessageDecoder.TryDecode(shortBuffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Makecode_high_byte_is_ignored_only_low_byte_used()
    {
        var buffer = RawKeyboard(makeCode: 0x011E, flags: 0);

        var ok = RawInputMessageDecoder.TryDecode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
    }
}
