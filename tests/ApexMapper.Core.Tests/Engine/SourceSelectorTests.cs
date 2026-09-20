using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class SourceSelectorTests
{
    private static KeySlot Slot(bool digital, float analog) => new(digital, analog, Gated: false, AnalogDriven: true);

    [Fact]
    public void Analog_drives_only_after_a_reading_at_rest()
    {
        var selector = new SourceSelector();
        // Key held at start: not at rest, so digital drives it.
        Assert.Equal(1f, selector.Select(Slot(true, 0.6f), analogAvailable: true));
        Assert.False(selector.UsingAnalog);
        // Released: at rest latches.
        Assert.Equal(0f, selector.Select(Slot(false, 0f), true));
        Assert.True(selector.UsingAnalog);
        Assert.Equal(0.4f, selector.Select(Slot(true, 0.4f), true));
        Assert.True(selector.UsingAnalog);
    }

    [Fact]
    public void Fault_mid_press_falls_back_to_digital_and_recovery_while_held_stays_digital()
    {
        var selector = new SourceSelector();
        selector.Select(Slot(false, 0f), true);
        selector.Select(Slot(true, 0.7f), true);
        Assert.True(selector.UsingAnalog);

        Assert.Equal(1f, selector.Select(Slot(true, float.NaN), analogAvailable: false));
        Assert.False(selector.UsingAnalog);

        // Sensor back but the key is still held: digital until released.
        Assert.Equal(1f, selector.Select(Slot(true, 0.7f), true));
        Assert.False(selector.UsingAnalog);

        Assert.Equal(0f, selector.Select(Slot(false, 0f), true));
        Assert.True(selector.UsingAnalog);
    }

    [Fact]
    public void Unavailable_analog_never_drives_even_at_rest()
    {
        var selector = new SourceSelector();
        Assert.Equal(0f, selector.Select(Slot(false, 0f), analogAvailable: false));
        Assert.False(selector.UsingAnalog);
        Assert.Equal(1f, selector.Select(Slot(true, 0f), analogAvailable: false));
        Assert.False(selector.UsingAnalog);
    }

    [Fact]
    public void Reset_drops_the_latch()
    {
        var selector = new SourceSelector();
        selector.Select(Slot(false, 0f), true);
        Assert.True(selector.UsingAnalog);
        selector.Reset();
        Assert.Equal(1f, selector.Select(Slot(true, 0.5f), true));
        Assert.False(selector.UsingAnalog);
    }
}
