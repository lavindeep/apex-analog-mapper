using ApexMapper.Core.Keys;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>One decoded Raw Input keyboard event, attributed to the device that produced it.</summary>
/// <param name="Device">The RAWINPUTHEADER device handle; zero for injected input.</param>
public readonly record struct RawKeyEvent(ScanCode Code, bool Down, nint Device, long Ticks);

/// <summary>
/// RAWKEYBOARD make code and flags to a <see cref="ScanCode"/>. Pure, so the table is
/// testable without a window. Events that carry no usable make code (overrun, the fake
/// shifts the keyboard sends around extended keys, a zero code) are dropped.
/// </summary>
public static class RawInputDecoder
{
    private const ushort Overrun = 0xFF;
    private const ushort LeftShift = 0x2A;
    private const ushort RightShift = 0x36;

    public static bool TryDecode(ushort makeCode, ushort flags, out ScanCode code, out bool down)
    {
        down = (flags & User32.RI_KEY_BREAK) == 0;
        code = default;
        if (makeCode is 0 or > 0xFF || makeCode == Overrun)
        {
            return false;
        }
        var e0 = (flags & User32.RI_KEY_E0) != 0;
        var e1 = (flags & User32.RI_KEY_E1) != 0;
        if (e0 && makeCode is LeftShift or RightShift)
        {
            return false;
        }
        var value = (ushort)(e1 ? 0xE100 | makeCode : e0 ? 0xE000 | makeCode : makeCode);
        code = new ScanCode(value);
        return true;
    }
}
