using ApexMapper.Input.Abstractions.RawInput;
using ApexMapper.Input.RawInput;

namespace ApexMapper.Input.Tests.RawInput;

public class RawInputKeyboardEnumeratorTests
{
    [Fact]
    public void Native_enumeration_returns_distinct_digital_SteelSeries_keyboard_paths()
    {
        var devices = new RawInputKeyboardEnumerator().Enumerate();

        devices.Select(d => d.DevicePath).Should().OnlyHaveUniqueItems();
        foreach (var device in devices)
        {
            device.Identity.Should().Be(RawInputDevicePath.Parse(device.DevicePath));
            device.Identity.VendorId.Should().Be(0x1038);
            device.Identity.ProductId.Should().NotBe(0);
            device.SupportsAnalog.Should().BeFalse();
        }
    }
}
