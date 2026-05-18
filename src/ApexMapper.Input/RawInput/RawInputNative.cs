using System.Runtime.InteropServices;

namespace ApexMapper.Input.RawInput;

internal static partial class RawInputNative
{
    internal const int WM_INPUT = 0x00FF;
    internal const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    internal const int WM_QUIT = 0x0012;

    internal const int GIDC_ARRIVAL = 1;
    internal const int GIDC_REMOVAL = 2;

    internal const uint RIDEV_REMOVE = 0x00000001;
    internal const uint RIDEV_INPUTSINK = 0x00000100;
    internal const uint RIDEV_DEVNOTIFY = 0x00002000;

    internal const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    internal const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    internal const uint RIM_TYPEKEYBOARD = 1;
    internal const uint RIM_TYPEMOUSE = 0;
    internal const uint RIM_TYPEHID = 2;

    internal const uint RID_INPUT = 0x10000003;
    internal const uint RID_HEADER = 0x10000005;

    internal const uint RIDI_DEVICENAME = 0x20000007;

    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUT
    {
        public RAWINPUTHEADER Header;
        public RAWKEYBOARD Keyboard;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] devices,
        uint deviceCount,
        uint cbSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    internal static partial uint GetRawInputDeviceInfoW(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(
        uint idThread,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();
}
