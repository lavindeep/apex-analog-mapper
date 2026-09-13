using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Devices;

public class DeviceCalibrationTests
{
    private static readonly KeyCalibration Measurement = new(KeyId.FromScanCode(0x11), 3000, 1000, 20);
    private static DiscoveredDevice Device(string? physicalId) => new(
        new DeviceIdentity(0x1038, 0x161C, null, "SteelSeries", "Apex Pro"),
        "raw/" + physicalId, true, PhysicalDeviceId: physicalId);

    [Theory]
    [InlineData("keyboard-a", "keyboard-a", "1.0", "1.0", true)]
    [InlineData("KEYBOARD-A", "keyboard-a", "1.0", "1.0", true)]
    [InlineData("keyboard-b", "keyboard-a", "1.0", "1.0", false)]
    [InlineData(null, null, "1.0", "1.0", false)]
    [InlineData(null, "keyboard-a", "1.0", "1.0", false)]
    [InlineData("keyboard-a", null, "1.0", "1.0", false)]
    [InlineData("keyboard-a", "keyboard-a", "2.0", "1.0", false)]
    public void Measurements_require_matching_physical_owner_and_firmware(
        string? physicalId, string? owner, string firmware, string savedFirmware, bool matches)
    {
        var device = Device(physicalId);
        var registry = new DeviceRegistry(device.Identity, new[] { Measurement })
        {
            CalibrationDeviceId = owner,
            CalibrationFirmware = savedFirmware,
        };
        var selector = new DeviceSelector(new InMemoryDeviceEnumerator(new[] { device }), () => registry, _ => { });
        selector.Initialize();

        var result = selector.GetCalibrations(device, firmware);

        if (matches) result.Should().Equal(Measurement);
        else result.Should().BeEmpty();
    }

    [Fact]
    public void Saved_measurements_keep_ownership_across_selection_changes_and_reload()
    {
        var first = Device("keyboard-a");
        var second = Device("keyboard-b");
        var enumerator = new InMemoryDeviceEnumerator(new[] { first, second });
        DeviceRegistry persisted = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => persisted, value => persisted = value);
        selector.Initialize();
        selector.Select(first);
        selector.SaveCalibrations("keyboard-a", "1.0", new[] { Measurement });
        selector.Select(second);
        selector.Unselect();

        var reloaded = new DeviceSelector(enumerator, () => persisted, _ => { });
        reloaded.Initialize();
        reloaded.GetCalibrations(first, "1.0").Should().Equal(Measurement);
        reloaded.GetCalibrations(second, "1.0").Should().BeEmpty();
        persisted.SelectedDevice.Should().BeNull();
    }

    [Fact]
    public void Failed_save_keeps_previous_measurements_in_memory_and_persistence()
    {
        var device = Device("keyboard-a");
        var original = new DeviceRegistry(device.Identity, new[] { Measurement })
        {
            CalibrationDeviceId = "keyboard-a",
            CalibrationFirmware = "1.0",
        };
        var selector = new DeviceSelector(new InMemoryDeviceEnumerator(new[] { device }), () => original,
            _ => throw new IOException("Disk unavailable"));
        selector.Initialize();
        var replacement = Measurement with { RestValue = 3500 };

        var save = () => selector.SaveCalibrations("keyboard-a", "2.0", new[] { replacement });

        save.Should().Throw<IOException>();
        selector.GetCalibrations(device, "1.0").Should().Equal(Measurement);
        selector.GetCalibrations(device, "2.0").Should().BeEmpty();
        original.Calibrations.Should().Equal(Measurement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keyboard-b")]
    public void Save_rejects_missing_or_other_physical_owner(string? selectedPhysicalId)
    {
        var device = Device(selectedPhysicalId);
        var writes = 0;
        var selector = new DeviceSelector(new InMemoryDeviceEnumerator(new[] { device }),
            () => new DeviceRegistry(device.Identity, Array.Empty<KeyCalibration>()), _ => writes++);
        selector.Initialize();

        var save = () => selector.SaveCalibrations("keyboard-a", "1.0", new[] { Measurement });

        save.Should().Throw<InvalidOperationException>();
        writes.Should().Be(0);
    }
}
