using ApexMapper.Core.Keys;

namespace ApexMapper.Input.Abstractions.Keys;

public static class ScanCodeEncoder
{
    public static KeyId Encode(byte prefix, byte baseScanCode)
    {
        if (prefix != 0x00 && prefix != 0xE0 && prefix != 0xE1)
        {
            throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "Prefix must be 0x00, 0xE0, or 0xE1.");
        }

        return new KeyId((ushort)((prefix << 8) | baseScanCode));
    }

    public static bool TryDecode(KeyId id, out byte prefix, out byte baseScanCode)
    {
        var high = (byte)(id.ScanCode >> 8);
        if (high != 0x00 && high != 0xE0 && high != 0xE1)
        {
            prefix = 0x00;
            baseScanCode = 0x00;
            return false;
        }

        prefix = high;
        baseScanCode = (byte)(id.ScanCode & 0xFF);
        return true;
    }
}
