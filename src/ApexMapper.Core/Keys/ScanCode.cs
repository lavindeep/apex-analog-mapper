namespace ApexMapper.Core.Keys;

/// <summary>
/// A Windows Set 1 scan code. Plain codes are 0x01..0xFF; extended codes carry an
/// 0xE0 or 0xE1 prefix in the high byte (0xE048 is Up, 0x48 is Numpad 8). Every valid
/// code maps to a stable slot index in 0..767 for array-backed lookups.
/// </summary>
public readonly record struct ScanCode
{
    public const int SlotCount = 768;

    public ushort Value { get; }

    public ScanCode(ushort value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Scan code must be 0x01..0xFF, 0xE001..0xE0FF, or 0xE101..0xE1FF.");
        }
        Value = value;
    }

    public static bool IsValid(ushort value)
    {
        var page = value >> 8;
        var code = value & 0xFF;
        return code != 0 && page is 0 or 0xE0 or 0xE1;
    }

    /// <summary>Array index: plain 0..255, E0 page 256..511, E1 page 512..767.</summary>
    public int Slot => (Value >> 8) switch
    {
        0xE0 => 256 + (Value & 0xFF),
        0xE1 => 512 + (Value & 0xFF),
        _ => Value,
    };

    public static ScanCode FromSlot(int slot)
    {
        if (slot is < 1 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
        return slot switch
        {
            < 256 => new ScanCode((ushort)slot),
            < 512 => new ScanCode((ushort)(0xE000 | (slot - 256))),
            _ => new ScanCode((ushort)(0xE100 | (slot - 512))),
        };
    }

    /// <summary>
    /// Ctrl, Alt, Win, and F12 can never be mapped: chords using them must reach the
    /// desktop, and F12 is half of the stop hotkey.
    /// </summary>
    public bool IsReserved => Value is 0x1D or 0xE01D or 0x38 or 0xE038 or 0xE05B or 0xE05C or 0x58;

    /// <summary>
    /// A key a binding may use: not reserved, and reported the same way by the keyboard
    /// hook, which blocks it, and by Raw Input, which the editor captures it with. Pause
    /// reaches Raw Input as E1 1D and then 45, whose second half reads as Num Lock, so
    /// the E1 page and Num Lock are both left out. The Korean language keys send a lone
    /// code with the break bit set (F1, F2), which the two report differently, so codes
    /// 71 and 72 and anything with that bit are left out too (stage 2 ledger M3).
    /// </summary>
    public bool IsBindable => !IsReserved && Value >> 8 != 0xE1 && Value != 0x45 && (Value & 0x7F) is not (0x71 or 0x72) && (Value & 0x80) == 0;

    public override string ToString() => $"0x{Value:X2}";
}
