using ApexMapper.Core.Keys;
using Xunit;

namespace ApexMapper.Core.Tests.Keys;

public class KeyStateStoreTests
{
    private const int W = 0x11;
    private const int Space = 0x39;

    [Fact]
    public void Starts_with_analog_unavailable_and_nothing_gated()
    {
        var store = new KeyStateStore();
        var slot = store.Read(W);
        Assert.False(slot.Digital);
        Assert.True(float.IsNaN(slot.Analog));
        Assert.False(slot.Gated);
        Assert.False(slot.AnalogDriven);
    }

    [Fact]
    public void Both_values_live_in_one_slot()
    {
        var store = new KeyStateStore();
        store.SetDigital(W, true);
        store.SetAnalog(W, 0.7f);
        var slot = store.Read(W);
        Assert.True(slot.Digital);
        Assert.Equal(0.7f, slot.Analog);
    }

    [Fact]
    public void Digital_key_up_clears_the_gate_of_a_digital_key()
    {
        var store = new KeyStateStore();
        store.SetDigital(Space, true);
        store.Gate(Space);
        store.SetDigital(Space, true);
        Assert.True(store.IsGated(Space));
        store.SetDigital(Space, false);
        Assert.False(store.IsGated(Space));
    }

    [Fact]
    public void Digital_key_up_never_clears_an_analog_driven_key()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.Gate(W);
        store.SetDigital(W, true);
        store.SetDigital(W, false);
        Assert.True(store.IsGated(W));
    }

    [Fact]
    public void Analog_at_rest_clears_an_analog_driven_key_and_a_pressed_reading_does_not()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.Gate(W);
        store.SetAnalog(W, 0.5f);
        Assert.True(store.IsGated(W));
        store.SetAnalog(W, 0f);
        Assert.False(store.IsGated(W));
        Assert.True(store.Read(W).AnalogAtRest);
    }

    [Fact]
    public void Gate_unknown_gates_analog_keys_and_held_digital_keys_only()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetDigital(Space, true);
        const int Q = 0x10;
        store.SetDigital(Q, false);

        store.GateUnknown();

        Assert.True(store.IsGated(W));
        Assert.True(store.IsGated(Space));
        Assert.False(store.IsGated(Q));
    }

    [Fact]
    public void Clear_all_keeps_digital_state_but_drops_gates_and_analog()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetDigital(W, true);
        store.SetAnalog(W, 0.4f);
        store.Gate(W);

        store.ClearAll();

        var slot = store.Read(W);
        Assert.True(slot.Digital);
        Assert.True(float.IsNaN(slot.Analog));
        Assert.False(slot.Gated);
        Assert.False(slot.AnalogDriven);
    }

    [Fact]
    public void Concurrent_writers_on_the_same_slot_lose_no_updates()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        var hook = new Thread(() =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                store.SetDigital(W, (i & 1) == 0);
            }
        });
        var engine = new Thread(() =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                store.SetAnalog(W, i / 100_000f);
            }
        });
        hook.Start();
        engine.Start();
        hook.Join();
        engine.Join();
        var slot = store.Read(W);
        Assert.False(slot.Digital);
        Assert.Equal(99_999 / 100_000f, slot.Analog);
        Assert.True(slot.AnalogDriven);
    }
}
