using ApexMapper.Input.Abstractions.RawInput;

namespace ApexMapper.Input.Abstractions.Tests.RawInput;

public class RawInputDeviceIdMapTests
{
    [Fact]
    public void Same_handle_gets_the_same_id_on_every_lookup()
    {
        var map = new RawInputDeviceIdMap();

        var first = map.GetOrAdd(0x1234);
        var again = map.GetOrAdd(0x1234);

        first.Should().BeGreaterThan(0);
        again.Should().Be(first);
    }

    [Fact]
    public void Distinct_handles_get_distinct_ids_even_when_low_bytes_collide()
    {
        var map = new RawInputDeviceIdMap();

        // Win32 raw-input handles are pointer-like and frequently share their
        // low byte; the map must not collapse them.
        var a = map.GetOrAdd(unchecked((nint)0x1_0000_0100));
        var b = map.GetOrAdd(unchecked((nint)0x2_0000_0100));

        a.Should().NotBe(b);
        a.Should().BeGreaterThan(0);
        b.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Zero_handle_maps_to_the_unknown_id()
    {
        var map = new RawInputDeviceIdMap();

        map.GetOrAdd(0).Should().Be(0);
    }

    [Fact]
    public void Remove_returns_the_assigned_id_and_forgets_the_handle()
    {
        var map = new RawInputDeviceIdMap();
        var id = map.GetOrAdd(0xBEEF);

        map.Remove(0xBEEF).Should().Be(id);
        map.Remove(0xBEEF).Should().Be(0);
    }

    [Fact]
    public void Reused_handle_after_remove_gets_a_fresh_id()
    {
        var map = new RawInputDeviceIdMap();
        var original = map.GetOrAdd(0xBEEF);
        map.Remove(0xBEEF);

        var reused = map.GetOrAdd(0xBEEF);

        reused.Should().NotBe(original);
        reused.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Remove_of_never_seen_handle_returns_the_unknown_id()
    {
        var map = new RawInputDeviceIdMap();

        map.Remove(0xF00D).Should().Be(0);
    }
}
