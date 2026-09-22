using System.Runtime.InteropServices;

namespace ApexMapper.Windows.Native;

/// <summary>
/// user32 declarations: low-level keyboard hook, Raw Input, message loop, window
/// creation for the message-only pump window, WinEvent foreground hook. Callbacks are
/// unmanaged function pointers so no delegate is marshalled per call.
/// </summary>
internal static unsafe partial class User32
{
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_INPUT = 0x00FF;
    public const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const uint WM_APP = 0x8000;

    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_UP = 0x80;

    public const ushort RI_KEY_BREAK = 0x01;
    public const ushort RI_KEY_E0 = 0x02;
    public const ushort RI_KEY_E1 = 0x04;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_DEVNOTIFY = 0x00002000;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const nuint GIDC_ARRIVAL = 1;
    public const nuint GIDC_REMOVAL = 2;
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    public const nint HWND_MESSAGE = -3;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nuint wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    /// <summary>The keyboard shape of RAWINPUT: header followed by RAWKEYBOARD.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTKEYBOARD
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nint, nint, nint> lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClassW(string lpClassName, nint hInstance);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterRawInputDevices(RAWINPUTDEVICE* pRawInputDevices, uint uiNumDevices, uint cbSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetRawInputData(nint hRawInput, uint uiCommand, void* pData, uint* pcbSize, uint cbSizeHeader);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetRawInputDeviceInfoW(nint hDevice, uint uiCommand, void* pData, uint* pcbSize);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    public const uint MAPVK_VSC_TO_VK_EX = 3;

    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);
}
