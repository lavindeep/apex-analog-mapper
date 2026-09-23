using ApexMapper.Core.Keys;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>What a key is called on the user's keyboard layout, so an AZERTY board shows Z where a US board shows W.</summary>
public static unsafe class KeyNames
{
    /// <summary>The layout's name for the key, or its scan code in hex when Windows has none (the E1 page, Pause).</summary>
    public static string Of(ScanCode key)
    {
        var page = key.Value >> 8;
        if (page == 0xE1)
        {
            return key.ToString();
        }
        var lParam = (key.Value & 0xFF) << 16 | (page == 0xE0 ? 1 << 24 : 0);
        var buffer = stackalloc char[64];
        var length = User32.GetKeyNameTextW(lParam, buffer, 64);
        return length > 0 ? new string(buffer, 0, length) : key.ToString();
    }
}
