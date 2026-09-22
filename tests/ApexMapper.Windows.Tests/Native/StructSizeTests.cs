using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;
using Xunit;

namespace ApexMapper.Windows.Tests.Native;

/// <summary>Documented x64 sizes. A wrong size here means a struct read through a pointer is misaligned.</summary>
public class StructSizeTests
{
    [Fact]
    public void Hook_and_raw_input_structs_match_the_documented_x64_sizes()
    {
        Assert.Equal(24, Marshal.SizeOf<User32.KBDLLHOOKSTRUCT>());
        Assert.Equal(16, Marshal.SizeOf<User32.RAWINPUTDEVICE>());
        Assert.Equal(24, Marshal.SizeOf<User32.RAWINPUTHEADER>());
        Assert.Equal(16, Marshal.SizeOf<User32.RAWKEYBOARD>());
        Assert.Equal(40, Marshal.SizeOf<User32.RAWINPUTKEYBOARD>());
        Assert.Equal(48, Marshal.SizeOf<User32.MSG>());
        Assert.Equal(80, Marshal.SizeOf<User32.WNDCLASSEXW>());
        Assert.Equal(40, Marshal.SizeOf<Injector.INPUT>());
        Assert.Equal(24, Marshal.SizeOf<Injector.KEYBDINPUT>());
    }

    [Fact]
    public void XInput_and_property_key_structs_match_the_documented_sizes()
    {
        Assert.Equal(12, Marshal.SizeOf<XInput.XINPUT_GAMEPAD>());
        Assert.Equal(16, Marshal.SizeOf<XInput.XINPUT_STATE>());
        Assert.Equal(20, Marshal.SizeOf<CfgMgr32.DEVPROPKEY>());
    }
}
