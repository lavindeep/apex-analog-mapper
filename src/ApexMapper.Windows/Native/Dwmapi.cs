using System.Runtime.InteropServices;

namespace ApexMapper.Windows.Native;

/// <summary>dwmapi: whether a window is cloaked, as a suspended Store app's frame is while still "visible".</summary>
internal static unsafe partial class Dwmapi
{
    public const uint DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);
}
