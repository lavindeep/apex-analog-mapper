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
/// One instance drives every keyboard on the single pump thread; the Pause
/// state machine is keyed by device id (so one keyboard's lead-in cannot
/// swallow another's real NumLock) and must be driven single-threaded.
/// </summary>
public sealed class RawInputMessageDecoder
{
    private const byte FakeShiftLeft = 0x2A;
    private const byte FakeShiftRight = 0x36;
    private const byte PauseLeadIn = 0x1D;  // E1 1D ... (Pause/Break prefix)
    private const byte PauseFiller = 0x45;  // ... 45 (also NumLock's bare scancode)

    // Device id of the keyboard whose E1-prefixed 0x1D (Pause lead-in) was last
    // decoded; the next bare 0x45 FROM THAT SAME DEVICE is the Pause filler and
    // must be swallowed. Null when no lead-in is pending. Keyed per device because
    // a single decoder instance serves every keyboard (RIDEV_INPUTSINK), so a
    // filler expectation set by one device must never consume another device's
    // real NumLock (nor leave the first device's real filler to leak as a phantom).
    private int? _pauseFillerDeviceId;

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

        // The Pause filler expectation is per-device: only the keyboard that sent
        // the E1 1D lead-in can produce the bare 0x45 filler that follows it. An
        // event from any OTHER device leaves the pending state untouched; an event
        // from the SAME device consumes it (whether or not it turns out to be the
        // filler), matching the single-decode semantics of the old flag.
        var expectingPauseFiller = _pauseFillerDeviceId == deviceId;
        if (expectingPauseFiller)
        {
            _pauseFillerDeviceId = null;
        }

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
            _pauseFillerDeviceId = deviceId;
        }

        var keyId = ScanCodeEncoder.Encode(prefix, baseScanCode);
        var isDown = (flags & 0x1) == 0;
        ev = new RawKeyEvent(keyId.ScanCode, isDown, timestampTicks, deviceId);
        return true;
    }
}
