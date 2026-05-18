using System.Buffers.Binary;
using ApexMapper.Input.Abstractions.Keys;
using ApexMapper.Input.Abstractions.Pipeline;

namespace ApexMapper.Input.Abstractions.RawInput;

public static class RawInputMessageDecoder
{
    public static bool TryDecode(
        ReadOnlySpan<byte> rawKeyboard,
        byte deviceHandleIndex,
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

        var keyId = ScanCodeEncoder.Encode(prefix, baseScanCode);
        var isDown = (flags & 0x1) == 0;
        ev = new RawKeyEvent(keyId.ScanCode, isDown, timestampTicks, deviceHandleIndex);
        return true;
    }
}
