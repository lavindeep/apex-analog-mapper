using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>
/// Windows lets only the process the user last worked with bring a window to the
/// front. A second launch of the app has that right and passes it on before asking the
/// first copy to show its window, or the first copy would only flash in the taskbar.
/// </summary>
public static class ForegroundPermission
{
    /// <summary>Lets any process take the foreground once. False when Windows refused, which leaves the flash.</summary>
    public static bool GrantToAnyProcess() => User32.AllowSetForegroundWindow(User32.ASFW_ANY);
}
