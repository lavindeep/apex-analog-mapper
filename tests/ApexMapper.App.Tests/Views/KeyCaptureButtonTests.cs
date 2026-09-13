using ApexMapper.App.Views.Profiles;
using ApexMapper.Core.Keys;

namespace ApexMapper.App.Tests.Views;

public sealed class KeyCaptureButtonTests
{
    [Theory]
    [InlineData(0x0D, 0x001C0000, 0x001C)] // Enter
    [InlineData(0x0D, 0x011C0000, 0xE01C)] // Numpad Enter shares VK_RETURN.
    [InlineData(0x26, 0x00480000, 0x0048)] // Numpad 8 with NumLock off
    [InlineData(0x26, 0x01480000, 0xE048)] // Up shares VK_UP.
    [InlineData(0x13, 0x00450000, 0xE11D)] // Pause matches the raw-input lead-in.
    public void DecodeKeyPreservesPhysicalScanCode(int virtualKey, long flags, int expected)
    {
        Assert.Equal(new KeyId((ushort)expected), KeyCaptureButton.DecodeKey(virtualKey, flags));
    }

    [Fact]
    public void DecodeKeyRejectsMessagesWithoutScanCode()
    {
        Assert.Null(KeyCaptureButton.DecodeKey(0, 0));
    }
}
