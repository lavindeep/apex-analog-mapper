using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Backends;

public class InterfacesSmokeTests
{
    private static DeviceIdentity MakeIdentity(string? serial = "SN-001") => new(
        VendorId: 0x1038,
        ProductId: 0x161C,
        SerialNumber: serial,
        ManufacturerName: "SteelSeries",
        ProductName: "Apex Pro");

    [Fact]
    public void BackendStatusChanged_supports_null_reason()
    {
        var evt = new BackendStatusChanged(BackendKind.RawInput, BackendStatus.Running, null);

        evt.Kind.Should().Be(BackendKind.RawInput);
        evt.Status.Should().Be(BackendStatus.Running);
        evt.Reason.Should().BeNull();
    }

    [Fact]
    public void BackendStatusChanged_carries_reason_string()
    {
        var evt = new BackendStatusChanged(BackendKind.HidAnalog, BackendStatus.FaultedAnalog, "stream closed");

        evt.Reason.Should().Be("stream closed");
    }

    [Fact]
    public void BackendStatus_enum_has_all_documented_values()
    {
        Enum.GetValues<BackendStatus>().Should().Contain(new[]
        {
            BackendStatus.Stopped,
            BackendStatus.Starting,
            BackendStatus.Running,
            BackendStatus.Degraded,
            BackendStatus.FaultedDigital,
            BackendStatus.FaultedAnalog,
            BackendStatus.Stopping,
        });
    }

    [Fact]
    public void BackendKind_enum_has_RawInput_and_HidAnalog()
    {
        Enum.GetValues<BackendKind>().Should().Contain(new[]
        {
            BackendKind.RawInput,
            BackendKind.HidAnalog,
        });
    }

    [Fact]
    public void DiscoveredDevice_records_with_identical_fields_are_equal()
    {
        var a = new DiscoveredDevice(MakeIdentity(), @"\\?\hid#vid_1038", SupportsAnalog: true);
        var b = new DiscoveredDevice(MakeIdentity(), @"\\?\hid#vid_1038", SupportsAnalog: true);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void DiscoveredDevice_records_with_different_paths_are_unequal()
    {
        var a = new DiscoveredDevice(MakeIdentity(), @"\\?\hid#vid_1038#a", SupportsAnalog: true);
        var b = new DiscoveredDevice(MakeIdentity(), @"\\?\hid#vid_1038#b", SupportsAnalog: true);

        a.Should().NotBe(b);
    }

    [Fact]
    public void RawInputDeviceChanged_attach_and_detach_carry_path()
    {
        var attach = new RawInputDeviceChanged(MakeIdentity(), Attached: true, DevicePath: @"\\?\path");
        var detach = new RawInputDeviceChanged(MakeIdentity(), Attached: false, DevicePath: @"\\?\path");

        attach.Attached.Should().BeTrue();
        detach.Attached.Should().BeFalse();
        attach.DevicePath.Should().Be(@"\\?\path");
    }

    [Fact]
    public void DeviceTopologyChanged_holds_change_kind_and_device()
    {
        var device = new DiscoveredDevice(MakeIdentity(), @"\\?\hid#x", SupportsAnalog: false);
        var evt = new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, device);

        evt.ChangeKind.Should().Be(DeviceTopologyChangeKind.Selected);
        evt.Device.Should().Be(device);
    }

    [Fact]
    public void DeviceTopologyChangeKind_has_all_documented_values()
    {
        Enum.GetValues<DeviceTopologyChangeKind>().Should().Contain(new[]
        {
            DeviceTopologyChangeKind.Attached,
            DeviceTopologyChangeKind.Detached,
            DeviceTopologyChangeKind.Selected,
            DeviceTopologyChangeKind.Unselected,
        });
    }

    [Fact]
    public void IInputBackend_is_async_disposable_so_hosts_can_await_shutdown()
    {
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(IInputBackend)).Should().BeTrue();
    }

    [Fact]
    public void IRawInputAdapter_extends_IInputBackend()
    {
        typeof(IInputBackend).IsAssignableFrom(typeof(IRawInputAdapter)).Should().BeTrue();
    }

    [Fact]
    public void IHidAnalogProbe_extends_IInputBackend()
    {
        typeof(IInputBackend).IsAssignableFrom(typeof(IHidAnalogProbe)).Should().BeTrue();
    }
}
