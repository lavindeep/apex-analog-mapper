using ApexMapper.App.Composition;
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
        var entries = facade.ListAll();

        entries.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        entries.Should().OnlyContain(d => d.PhysicalDeviceId == null);
        facade.SelectPrimary(entries[1].Id);

        selector.SelectedDevice.Should().Be(second);
        facade.ListAll().Should().ContainSingle(d => d.IsPrimary);
        facade.ListAll()[1].IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void Physical_metadata_is_presentation_only_and_keeps_each_source_selectable()
    {
        const string path = @"\\?\HID#VID_1038&PID_1614&MI_00&Col02#source";
        var first = new DiscoveredDevice(new DeviceIdentity(0x1038, 0x1614, null, null, path),
            path, false, "SteelSeries Apex Pro TKL", "physical-keyboard");
        var secondPath = path.Replace("MI_00", "MI_02");
        var second = first with { Identity = first.Identity with { ProductName = secondPath }, DevicePath = secondPath };
        var selector = new DeviceSelector(new FixedEnumerator(first, second),
            () => new DeviceRegistry(null, []), _ => { });
        selector.Initialize();
        var facade = new DeviceSelectorFacade(selector);

        var entries = facade.ListAll();
        entries.Should().OnlyContain(d => d.DisplayName == "SteelSeries Apex Pro TKL"
            && d.PhysicalDeviceId == "physical-keyboard");
        entries.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        entries[0].DevicePath.Should().Be(path);
        entries[1].SourceLabel.Should().Be("Interface 02, collection 02");
        facade.SelectPrimary(entries[1].Id);
        selector.SelectedDevice.Should().Be(second);
    }

    [Fact]
    public void Missing_metadata_does_not_show_raw_path_as_product_name()
    {
        const string path = @"\\?\HID#VID_1038&PID_1614&MI_00&Col02#source";
        var device = new DiscoveredDevice(new DeviceIdentity(0x1038, 0x1614, null, null, path), path, false);
        var selector = new DeviceSelector(new FixedEnumerator(device),
            () => new DeviceRegistry(null, []), _ => { });
        selector.Initialize();

        var entry = new DeviceSelectorFacade(selector).ListAll().Single();
        entry.DisplayName.Should().Be("SteelSeries keyboard (1614)");
        entry.DevicePath.Should().Be(path);
        entry.PhysicalDeviceId.Should().BeNull();
    }

    private sealed class FixedEnumerator(params DiscoveredDevice[] devices) : IDeviceEnumerator
    {
        public IReadOnlyList<DiscoveredDevice> Enumerate() => devices;
    }
}
