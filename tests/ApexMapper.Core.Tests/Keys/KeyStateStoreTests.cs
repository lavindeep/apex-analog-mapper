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
    public void Digital_key_up_clears_the_gate_of_a_digital_key_and_a_repeat_does_not()
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
    public void Hook_events_never_clear_an_analog_driven_key_while_its_reading_is_available()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetAnalog(W, 0.5f);
        store.Gate(W);
        store.SetDigital(W, true);
        store.SetDigital(W, false);
        store.SetDigital(W, true);
        Assert.True(store.IsGated(W));
    }

    [Fact]
    public void Hook_events_clear_an_analog_driven_key_while_its_reading_is_unavailable()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.Gate(W);
        // Never read: a press from a known-up key is a new press.
        store.SetDigital(W, true);
        Assert.False(store.IsGated(W));

        // Reading lost mid-session while gated and held: the repeat keeps the gate, the release clears it.
        store.SetAnalog(W, 0.5f);
        store.Gate(W);
        store.SetAnalog(W, float.NaN);
        store.SetDigital(W, true);
        Assert.True(store.IsGated(W));
        store.SetDigital(W, false);
        Assert.False(store.IsGated(W));
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
    public void A_zero_reading_does_not_clear_a_key_the_profile_drives_digitally()
    {
        var store = new KeyStateStore();
        store.SetDigital(Space, true);
        store.Gate(Space);
        store.SetAnalog(Space, 0f);
        Assert.True(store.IsGated(Space));
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
    public void Clear_all_returns_every_cell_to_its_initial_state()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetDigital(W, true);
        store.SetAnalog(W, 0.4f);
        store.Gate(W);

        store.ClearAll();

        var slot = store.Read(W);
        Assert.False(slot.Digital);
        Assert.True(float.IsNaN(slot.Analog));
        Assert.False(slot.Gated);
        Assert.False(slot.AnalogDriven);
    }

    [Fact]
    public void Clear_analog_driven_drops_only_that_flag()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetAnalog(W, 0.4f);
        store.Gate(W);
        store.ClearAnalogDriven();
        var slot = store.Read(W);
        Assert.False(slot.AnalogDriven);
        Assert.True(slot.Gated);
        Assert.Equal(0.4f, slot.Analog);
    }

    [Fact]
    public void Concurrent_writers_on_the_same_slot_lose_no_updates()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetAnalog(W, 0f);
        using var start = new ManualResetEventSlim(false);
        var stop = 0;
        string? violation = null;

        var hook = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < 200_000; i++)
            {
                store.SetDigital(W, (i & 1) == 0);
            }
        });
        var engine = new Thread(() =>
        {
            start.Wait();
            for (var i = 1; i <= 200_000; i++)
            {
                store.SetAnalog(W, i / 200_000f);
            }
        });
        // A lost update would push the analog value backwards or drop the flag.
        var reader = new Thread(() =>
        {
            start.Wait();
            var last = 0f;
            while (Volatile.Read(ref stop) == 0)
            {
                var slot = store.Read(W);
                if (slot.Analog < last || !slot.AnalogDriven)
                {
                    violation = $"analog {slot.Analog} after {last}, driven {slot.AnalogDriven}";
                    return;
                }
                last = slot.Analog;
            }
        });
        hook.Start();
        engine.Start();
        reader.Start();
        start.Set();
        hook.Join();
        engine.Join();
        Volatile.Write(ref stop, 1);
        reader.Join();

        Assert.Null(violation);
        var final = store.Read(W);
        Assert.False(final.Digital);
        Assert.Equal(1f, final.Analog);
        Assert.True(final.AnalogDriven);
    }

    [Fact]
    public void Gate_unknown_racing_the_hook_never_corrupts_a_cell()
    {
        var store = new KeyStateStore();
        store.SetAnalogDriven(W, true);
        store.SetAnalog(W, 0.5f);
        using var start = new ManualResetEventSlim(false);
        var hook = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < 50_000; i++)
            {
                store.SetDigital(Space, (i & 1) == 0);
            }
        });
        var session = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < 200; i++)
            {
                store.GateUnknown();
                store.ClearAll();
                store.SetAnalogDriven(W, true);
                store.SetAnalog(W, 0.5f);
            }
        });
        hook.Start();
        session.Start();
        start.Set();
        hook.Join();
        session.Join();
        var w = store.Read(W);
        Assert.Equal(0.5f, w.Analog);
        Assert.True(w.AnalogDriven);
        Assert.False(store.Read(Space).Digital);
    }
}
