using System.Buffers.Binary;
using ApexMapper.Input.Abstractions.Keys;
using ApexMapper.Input.Abstractions.Pipeline;

namespace ApexMapper.Input.Abstractions.RawInput;

/// <summary>
/// Decodes RAWKEYBOARD payloads into <see cref="RawKeyEvent"/>s.
/// <para>
/// Stateful by necessity: keyboards emit a couple of synthetic scancodes that a
/// stateless decoder would surface as phantom key presses.
/// <list type="bullet">
/// <item>The Pause/Break key arrives as an E1-prefixed 0x1D lead-in followed by
/// a bare 0x45 — and 0x45 is also NumLock's scancode. The lead-in flags the
/// next bare 0x45 so it is swallowed instead of firing a phantom NumLock.</item>
/// <item>Keyboards bracket the extended navigation block (arrows, Ins/Del,
/// PrtSc) with E0 2A / E0 36 "fake shift" make/break pairs; those are not real
/// shift presses and are dropped.</item>
/// </list>
/// </para>
/// One instance per input source — the Pause state machine is single-threaded
/// and must be driven from a single pump thread.
/// </summary>
public sealed class RawInputMessageDecoder
{
    private const byte FakeShiftLeft = 0x2A;
    private const byte FakeShiftRight = 0x36;
    private const byte PauseLeadIn = 0x1D;  // E1 1D ... (Pause/Break prefix)
    private const byte PauseFiller = 0x45;  // ... 45 (also NumLock's bare scancode)

    // Set once an E1-prefixed 0x1D (Pause lead-in) is decoded; the very next
    // event, if a bare 0x45, is the Pause filler and must be swallowed.
    private bool _expectPauseFiller;

    public bool TryDecode(
        ReadOnlySpan<byte> rawKeyboard,
        int deviceId,
        long timestampTicks,
        out RawKeyEvent ev)
    {
        ev = default;
        if (rawKeyboard.Length < 4) return false;

        var makeCode = BinaryPrimitives.ReadUInt16LittleEndian(rawKeyboard[..2]);
        var flags    = BinaryPrimitives.ReadUInt16LittleEndian(rawKeyboard.Slice(2, 2));

        if (makeCode == 0xFF) return false;
        var baseScanCode = (byte)(makeCode & 0xFF);
        if (baseScanCode == 0) return false;

        byte prefix = 0x00;
        if ((flags & 0x4) != 0) prefix = 0xE1;
        else if ((flags & 0x2) != 0) prefix = 0xE0;

        // Consume the "next event is the Pause filler" expectation exactly once.
        var expectingPauseFiller = _expectPauseFiller;
        _expectPauseFiller = false;

        // Pause/Break: the bare 0x45 trailing an E1 1D lead-in shares NumLock's
        // scancode. Swallow it so Pause never leaks a phantom NumLock event.
        if (expectingPauseFiller && prefix == 0x00 && baseScanCode == PauseFiller)
        {
            return false;
        }

        // Fake-shift make/break pairs (E0 2A, E0 36) around the extended
        // navigation block are not real Shift presses.
        if (prefix == 0xE0 && (baseScanCode == FakeShiftLeft || baseScanCode == FakeShiftRight))
        {
            return false;
        }

        if (prefix == 0xE1 && baseScanCode == PauseLeadIn)
        {
            _expectPauseFiller = true;
        }

        var keyId = ScanCodeEncoder.Encode(prefix, baseScanCode);
        var isDown = (flags & 0x1) == 0;
        ev = new RawKeyEvent(keyId.ScanCode, isDown, timestampTicks, deviceId);
        return true;
    }
}
