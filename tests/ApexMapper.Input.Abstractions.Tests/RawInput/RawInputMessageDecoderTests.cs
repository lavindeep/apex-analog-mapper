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

    // Each stateless case uses a fresh decoder; only the Pause sequence needs a
    // shared instance to carry the E1 lead-in state across two decodes.
    private static bool Decode(byte[] buffer, int deviceId, long timestampTicks, out RawKeyEvent ev)
        => new RawInputMessageDecoder().TryDecode(buffer, deviceId, timestampTicks, out ev);

    [Fact]
    public void Normal_key_down_decodes_scancode_and_isdown_true()
    {
        var buffer = RawKeyboard(makeCode: 0x1E, flags: 0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
        ev.IsDown.Should().BeTrue();
    }

    [Fact]
    public void Normal_key_up_decodes_isdown_false()
    {
        var buffer = RawKeyboard(makeCode: 0x1E, flags: RI_KEY_BREAK);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
        ev.IsDown.Should().BeFalse();
    }

    [Fact]
    public void E0_extended_key_down_produces_E0_prefixed_scancode()
    {
        var buffer = RawKeyboard(makeCode: 0x4D, flags: RI_KEY_E0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0xE04D);
        ev.IsDown.Should().BeTrue();
    }

    [Fact]
    public void E0_extended_key_up_produces_E0_prefix_and_isdown_false()
    {
        var buffer = RawKeyboard(makeCode: 0x4D, flags: (ushort)(RI_KEY_E0 | RI_KEY_BREAK));

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0xE04D);
        ev.IsDown.Should().BeFalse();
    }

    [Fact]
    public void E1_pause_key_produces_E1_prefixed_scancode()
    {
        var buffer = RawKeyboard(makeCode: 0x1D, flags: RI_KEY_E1);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0xE11D);
        ev.IsDown.Should().BeTrue();
    }

    [Fact]
    public void Fake_shift_makecode_0xFF_is_filtered_out()
    {
        var buffer = RawKeyboard(makeCode: 0xFF, flags: 0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Zero_scancode_is_filtered_out()
    {
        var buffer = RawKeyboard(makeCode: 0x0000, flags: 0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Timestamp_and_device_id_flow_through_to_event()
    {
        var buffer = RawKeyboard(makeCode: 0x1E, flags: 0);

        var ok = Decode(buffer, deviceId: 0x4_0000, timestampTicks: 12345L, out var ev);

        ok.Should().BeTrue();
        ev.TimestampTicks.Should().Be(12345L);
        ev.DeviceId.Should().Be(0x4_0000);
    }

    [Fact]
    public void Short_buffer_under_four_bytes_returns_false()
    {
        var shortBuffer = new byte[3];

        var ok = Decode(shortBuffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Makecode_high_byte_is_ignored_only_low_byte_used()
    {
        var buffer = RawKeyboard(makeCode: 0x011E, flags: 0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
    }

    [Theory]
    [InlineData((ushort)0x2A)] // E0 2A fake LeftShift
    [InlineData((ushort)0x36)] // E0 36 fake RightShift
    public void E0_fake_shift_make_is_swallowed(ushort makeCode)
    {
        var buffer = RawKeyboard(makeCode, flags: RI_KEY_E0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Theory]
    [InlineData((ushort)0x2A)]
    [InlineData((ushort)0x36)]
    public void E0_fake_shift_break_is_swallowed(ushort makeCode)
    {
        var buffer = RawKeyboard(makeCode, flags: (ushort)(RI_KEY_E0 | RI_KEY_BREAK));

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeFalse();
        ev.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Real_left_shift_without_E0_still_decodes()
    {
        var buffer = RawKeyboard(makeCode: 0x2A, flags: 0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x002A);
    }

    [Fact]
    public void Pause_filler_numlock_scancode_is_swallowed_after_E1_lead_in()
    {
        // Pause arrives as E1 1D (lead-in) then a bare 0x45 (== NumLock's scancode).
        var decoder = new RawInputMessageDecoder();

        var leadIn = RawKeyboard(makeCode: 0x1D, flags: RI_KEY_E1);
        decoder.TryDecode(leadIn, deviceId: 0, timestampTicks: 0, out var leadEv).Should().BeTrue();
        leadEv.ScanCode.Should().Be((ushort)0xE11D);

        var filler = RawKeyboard(makeCode: 0x45, flags: 0);
        var ok = decoder.TryDecode(filler, deviceId: 0, timestampTicks: 0, out var fillerEv);

        ok.Should().BeFalse();
        fillerEv.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Standalone_numlock_decodes_when_no_pause_lead_in_precedes_it()
    {
        var buffer = RawKeyboard(makeCode: 0x45, flags: 0);

        var ok = Decode(buffer, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x0045);
    }

    [Fact]
    public void Pause_lead_in_does_not_swallow_a_following_non_filler_key()
    {
        var decoder = new RawInputMessageDecoder();

        var leadIn = RawKeyboard(makeCode: 0x1D, flags: RI_KEY_E1);
        decoder.TryDecode(leadIn, deviceId: 0, timestampTicks: 0, out _).Should().BeTrue();

        // A normal key right after the lead-in must pass through untouched.
        var normal = RawKeyboard(makeCode: 0x1E, flags: 0);
        var ok = decoder.TryDecode(normal, deviceId: 0, timestampTicks: 0, out var ev);

        ok.Should().BeTrue();
        ev.ScanCode.Should().Be((ushort)0x001E);
    }

    [Fact]
    public void Pause_lead_in_from_one_device_does_not_swallow_another_devices_numlock()
    {
        // One decoder serves every keyboard (RIDEV_INPUTSINK). A device's Pause
        // lead-in must not consume a DIFFERENT device's real NumLock, nor leak the
        // lead-in device's real filler as a phantom NumLock afterwards.
        var decoder = new RawInputMessageDecoder();
        const int deviceA = 1;
        const int deviceB = 2;

        // Device A emits the Pause lead-in (E1 1D).
        decoder.TryDecode(RawKeyboard(0x1D, RI_KEY_E1), deviceA, 0, out _).Should().BeTrue();

        // Device B's real NumLock (bare 0x45) must pass through untouched.
        var okB = decoder.TryDecode(RawKeyboard(0x45, 0), deviceB, 0, out var evB);
        okB.Should().BeTrue();
        evB.ScanCode.Should().Be((ushort)0x0045);
        evB.DeviceId.Should().Be(deviceB);

        // Device A's actual Pause filler (bare 0x45) is still swallowed.
        var okA = decoder.TryDecode(RawKeyboard(0x45, 0), deviceA, 0, out var evA);
        okA.Should().BeFalse();
        evA.Should().Be(default(RawKeyEvent));
    }

    [Fact]
    public void Interleaved_lead_in_does_not_swallow_another_devices_numlock_break()
    {
        // Cross-device interleave must not desynchronize make/break: a real NumLock
        // DOWN followed by another device's lead-in must not swallow the matching
        // NumLock BREAK, or the key latches down forever.
        var decoder = new RawInputMessageDecoder();
        const int deviceA = 1;
        const int deviceB = 2;

        // Device B presses NumLock for real (no lead-in) -> DOWN emits.
        decoder.TryDecode(RawKeyboard(0x45, 0), deviceB, 0, out var down).Should().BeTrue();
        down.IsDown.Should().BeTrue();

        // Device A's Pause lead-in arrives in between.
        decoder.TryDecode(RawKeyboard(0x1D, RI_KEY_E1), deviceA, 0, out _).Should().BeTrue();

        // Device B releases NumLock -> BREAK must still emit.
        var okUp = decoder.TryDecode(RawKeyboard(0x45, RI_KEY_BREAK), deviceB, 0, out var up);
        okUp.Should().BeTrue();
        up.ScanCode.Should().Be((ushort)0x0045);
        up.IsDown.Should().BeFalse();
    }
}
