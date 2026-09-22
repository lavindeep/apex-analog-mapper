using System.Runtime.InteropServices;

namespace ApexMapper.Windows.Tests.Native;

/// <summary>
/// <c>SendInput</c> by scan code, for the hardware tests only: the product never
/// injects a keystroke. Injected events reach whatever window is foreground, so these
/// tests type into it.
/// </summary>
internal static unsafe partial class Injector
{
    public const int VK_W = 0x57;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    /// <summary>INPUT with the keyboard member; the union is padded to the mouse member's size.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    /// <summary>Injects one key event by scan code (0xE0xx for extended keys).</summary>
    public static bool SendScanCode(ushort scanCode, bool down)
    {
        var extended = (scanCode & 0xFF00) == 0xE000;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wScan = (ushort)(scanCode & 0xFF),
                dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP),
            },
        };
        return SendInput(1, &input, sizeof(INPUT)) == 1;
    }

    /// <summary>The asynchronous key state, which a swallowed event never reaches and a passed one does.</summary>
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
