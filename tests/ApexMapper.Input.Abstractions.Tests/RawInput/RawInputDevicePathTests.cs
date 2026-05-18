using ApexMapper.Input.Abstractions.RawInput;

namespace ApexMapper.Input.Abstractions.Tests.RawInput;

public class RawInputDevicePathTests
{
    [Fact]
    public void Parses_uppercase_vid_and_pid_from_hid_interface_path()
    {
        const string path = @"\\?\HID#VID_1038&PID_161C&MI_01#7&abc123&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

        var identity = RawInputDevicePath.Parse(path);

        identity.VendorId.Should().Be(0x1038);
        identity.ProductId.Should().Be(0x161C);
    }

    [Fact]
    public void Parses_lowercase_vid_and_pid()
    {
        const string path = @"\\?\hid#vid_1038&pid_161c&mi_01#7&abc&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

        var identity = RawInputDevicePath.Parse(path);

        identity.VendorId.Should().Be(0x1038);
        identity.ProductId.Should().Be(0x161C);
    }

    [Fact]
    public void Stores_original_path_as_product_name_fallback()
    {
        const string path = @"\\?\HID#VID_1038&PID_161C&MI_01#7&abc&0&0000#{guid}";

        var identity = RawInputDevicePath.Parse(path);

        identity.ProductName.Should().Be(path);
    }

    [Fact]
    public void Serial_and_manufacturer_default_to_null()
    {
        const string path = @"\\?\HID#VID_1038&PID_161C&MI_01#7&abc&0&0000#{guid}";

        var identity = RawInputDevicePath.Parse(path);

        identity.SerialNumber.Should().BeNull();
        identity.ManufacturerName.Should().BeNull();
    }

    [Fact]
    public void Path_without_vid_pid_returns_zeroed_identity()
    {
        const string path = @"\\?\ROOT#SOMETHING#0000#{guid}";

        var identity = RawInputDevicePath.Parse(path);

        identity.VendorId.Should().Be(0);
        identity.ProductId.Should().Be(0);
        identity.ProductName.Should().Be(path);
    }

    [Fact]
    public void Null_path_returns_zeroed_identity_with_null_product_name()
    {
        var identity = RawInputDevicePath.Parse(null);

        identity.VendorId.Should().Be(0);
        identity.ProductId.Should().Be(0);
        identity.ProductName.Should().BeNull();
    }

    [Fact]
    public void Empty_path_returns_zeroed_identity_with_empty_product_name()
    {
        var identity = RawInputDevicePath.Parse(string.Empty);

        identity.VendorId.Should().Be(0);
        identity.ProductId.Should().Be(0);
        identity.ProductName.Should().Be(string.Empty);
    }

    [Fact]
    public void Mixed_case_hex_in_pid_is_accepted()
    {
        const string path = @"\\?\HID#VID_AbCd&PID_eF01&MI_00#x&y&0&0#{guid}";

        var identity = RawInputDevicePath.Parse(path);

        identity.VendorId.Should().Be(0xABCD);
        identity.ProductId.Should().Be(0xEF01);
    }

    [Fact]
    public void Only_vid_present_yields_vid_but_zero_pid()
    {
        const string path = @"\\?\HID#VID_1038&XX_FFFF#stuff";

        var identity = RawInputDevicePath.Parse(path);

        identity.VendorId.Should().Be(0x1038);
        identity.ProductId.Should().Be(0);
    }

    [Fact]
    public void Only_pid_present_yields_pid_but_zero_vid()
    {
        const string path = @"\\?\HID#XX_FFFF&PID_161C#stuff";

        var identity = RawInputDevicePath.Parse(path);

        identity.VendorId.Should().Be(0);
        identity.ProductId.Should().Be(0x161C);
    }
}
