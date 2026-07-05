using ApexMapper.Input.Abstractions.Pipeline;

namespace ApexMapper.Input.Abstractions.Tests.Pipeline;

public class RawKeyEventTests
{
    [Fact]
    public void Two_events_with_same_fields_are_equal()
    {
        var a = new RawKeyEvent(ScanCode: 0xE04D, IsDown: true, TimestampTicks: 12345L, DeviceId: 2);
        var b = new RawKeyEvent(ScanCode: 0xE04D, IsDown: true, TimestampTicks: 12345L, DeviceId: 2);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Differing_field_breaks_equality()
    {
        var a = new RawKeyEvent(0x001E, true, 100L, 0);
        var b = new RawKeyEvent(0x001E, false, 100L, 0);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Differing_device_id_breaks_equality()
    {
        var a = new RawKeyEvent(0x001E, true, 100L, 1);
        var b = new RawKeyEvent(0x001E, true, 100L, 2);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Field_round_trip_preserves_values()
    {
        var evt = new RawKeyEvent(
            ScanCode: 0xE01D,
            IsDown: true,
            TimestampTicks: 987_654_321L,
            DeviceId: 0x1234_5678);

        evt.ScanCode.Should().Be(0xE01D);
        evt.IsDown.Should().BeTrue();
        evt.TimestampTicks.Should().Be(987_654_321L);
        evt.DeviceId.Should().Be(0x1234_5678);
    }

    [Fact]
    public void Default_event_has_zeroed_fields()
    {
        var evt = default(RawKeyEvent);

        evt.ScanCode.Should().Be((ushort)0);
        evt.IsDown.Should().BeFalse();
        evt.TimestampTicks.Should().Be(0L);
        evt.DeviceId.Should().Be(0);
    }
}
