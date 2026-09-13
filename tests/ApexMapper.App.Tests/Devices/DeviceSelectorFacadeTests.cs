using ApexMapper.App.Composition;
using ApexMapper.App.ViewModels.Devices;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Persistence.Devices;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Devices;

public sealed class DeviceSelectorFacadeTests
{
    [Fact]
    public void Interfaces_with_the_same_vid_pid_remain_individually_selectable()
    {
        var first = new DiscoveredDevice(new DeviceIdentity(0x1038, 0x1614, null, null, "interface-1"), "interface-1", false);
        var second = new DiscoveredDevice(new DeviceIdentity(0x1038, 0x1614, null, null, "interface-2"), "interface-2", false);
        var selector = new DeviceSelector(new FixedEnumerator(first, second),
            () => new DeviceRegistry(null, []), _ => { });
        selector.Initialize();
        var facade = new DeviceSelectorFacade(selector);
        var picker = new DevicePickerViewModel(facade);

        picker.Devices.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        picker.MakePrimaryCommand.Execute(picker.Devices[1].Id);

        selector.SelectedDevice.Should().Be(second);
        picker.Devices.Should().ContainSingle(d => d.IsPrimary);
        picker.Devices[1].IsPrimary.Should().BeTrue();
    }

    private sealed class FixedEnumerator(params DiscoveredDevice[] devices) : IDeviceEnumerator
    {
        public IReadOnlyList<DiscoveredDevice> Enumerate() => devices;
    }
}
