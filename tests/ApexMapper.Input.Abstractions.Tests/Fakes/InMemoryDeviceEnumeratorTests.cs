using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Fakes;

public class InMemoryDeviceEnumeratorTests
{
    private static DiscoveredDevice Dev(string path, bool analog = true) => new(
        new DeviceIdentity(0x1038, 0x161C, path, "SteelSeries", "Apex Pro"),
        path,
        analog);

    [Fact]
    public void Initial_devices_are_returned_by_Enumerate()
    {
        var a = Dev("a");
        var b = Dev("b");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });

        enumerator.Enumerate().Should().Equal(a, b);
    }

    [Fact]
    public void Add_makes_device_visible_to_Enumerate()
    {
        var enumerator = new InMemoryDeviceEnumerator(Array.Empty<DiscoveredDevice>());
        var d = Dev("new");

        enumerator.Add(d);

        enumerator.Enumerate().Should().ContainSingle().Which.Should().Be(d);
    }

    [Fact]
    public void Remove_drops_device_from_Enumerate()
    {
        var a = Dev("a");
        var b = Dev("b");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });

        enumerator.Remove(a).Should().BeTrue();

        enumerator.Enumerate().Should().ContainSingle().Which.Should().Be(b);
    }

    [Fact]
    public void Enumerate_returns_a_snapshot_so_later_mutation_does_not_affect_it()
    {
        var enumerator = new InMemoryDeviceEnumerator(new[] { Dev("a") });
        var snapshot = enumerator.Enumerate();

        enumerator.Add(Dev("b"));

        snapshot.Should().HaveCount(1);
    }
}
