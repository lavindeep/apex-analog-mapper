using System.Runtime.InteropServices;

namespace ApexMapper.Windows.Native;

/// <summary>xinput1_4: read back the virtual pad the way a game does.</summary>
internal static partial class XInput
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_DEVICE_NOT_CONNECTED = 1167;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [LibraryImport("xinput1_4.dll")]
    public static partial uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
}
