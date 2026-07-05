using ApexMapper.Core.Keys;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Keys;

public class KeyStateStoreGatingTests
{
    private static KeyId K(ushort s) => KeyId.FromScanCode(s);

    private static KeyStateStore MakeIndexed(params KeyId[] keys) => new(new KeyIndex(keys));

    public static TheoryData<bool> Modes => new() { true, false };

    private static KeyStateStore MakeStore(bool indexed, params KeyId[] keys) =>
        indexed ? MakeIndexed(keys) : new KeyStateStore();

    [Theory]
    [MemberData(nameof(Modes))]
    public void GateHeldKeys_zeroes_held_key_and_marks_it_gated(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);
        store.Set(key, 1f, KeyProvenance.Digital);

        store.GateHeldKeys();

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GateHeldKeys_does_not_gate_keys_at_rest(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);

        store.GateHeldKeys();

        store.IsGated(key).Should().BeFalse();
        store.Set(key, 1f, KeyProvenance.Digital);
        store.Get(key).Value.Should().Be(1f);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Gated_key_ignores_pressed_digital_writes(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);
        store.Set(key, 1f, KeyProvenance.Digital);
        store.GateHeldKeys();

        // Raw Input auto-repeat down while gated must not re-press.
        store.Set(key, 1f, KeyProvenance.Digital);

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Gated_key_ignores_pressed_analog_writes(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);
        store.Set(key, 0.8f, KeyProvenance.Analog);
        store.GateHeldKeys();

        store.Set(key, 0.6f, KeyProvenance.Analog);

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Release_clears_gate_and_next_press_works_normally(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);
        store.Set(key, 1f, KeyProvenance.Digital);
        store.GateHeldKeys();

        store.Set(key, 0f, KeyProvenance.Digital);

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeFalse();

        store.Set(key, 1f, KeyProvenance.Digital);
        store.Get(key).Value.Should().Be(1f);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Analog_zero_also_clears_gate(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);
        store.Set(key, 0.9f, KeyProvenance.Analog);
        store.GateHeldKeys();

        // A fully-released analog key is released: exact 0 clears the gate.
        store.Set(key, 0f, KeyProvenance.Analog);

        store.IsGated(key).Should().BeFalse();
        store.Set(key, 0.5f, KeyProvenance.Analog);
        store.Get(key).Value.Should().BeApproximately(0.5f, 1e-6f);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Held_key_across_gate_transition_full_sequence(bool indexed)
    {
        var key = K(0x1E);
        var store = MakeStore(indexed, key);

        // Hold at 1.0.
        store.Set(key, 1f, KeyProvenance.Digital);
        store.Get(key).Value.Should().Be(1f);

        // Gate transition: output must drop immediately.
        store.GateHeldKeys();
        store.Get(key).Value.Should().Be(0f);

        // Physical release: still zero, gate cleared.
        store.Set(key, 0f, KeyProvenance.Digital);
        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeFalse();

        // Next press drives output again.
        store.Set(key, 1f, KeyProvenance.Digital);
        store.Get(key).Value.Should().Be(1f);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Provenance_sweep_gates_only_matching_keys(bool indexed)
    {
        var analogKey = K(0x11);
        var digitalKey = K(0x1E);
        var store = MakeStore(indexed, analogKey, digitalKey);
        store.Set(analogKey, 0.7f, KeyProvenance.Analog);
        store.Set(digitalKey, 1f, KeyProvenance.Digital);

        store.GateHeldKeys(KeyProvenance.Analog);

        store.Get(analogKey).Value.Should().Be(0f);
        store.IsGated(analogKey).Should().BeTrue();
        store.Get(digitalKey).Value.Should().Be(1f);
        store.IsGated(digitalKey).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Reset_clears_gates(bool indexed)
    {
        var key = K(0x11);
        var store = MakeStore(indexed, key);
        store.Set(key, 1f, KeyProvenance.Digital);
        store.GateHeldKeys();

        store.Reset();

        store.IsGated(key).Should().BeFalse();
        store.Set(key, 1f, KeyProvenance.Digital);
        store.Get(key).Value.Should().Be(1f);
    }

    [Fact]
    public async Task Indexed_gate_racing_pressed_writes_never_leaves_stuck_output()
    {
        var key = K(0x11);
        var idx = new KeyIndex(new[] { key });
        var store = new KeyStateStore(idx);

        const int rounds = 500;
        for (var round = 0; round < rounds; round++)
        {
            // The key is already held before the sweep starts, so the sweep
            // must observe it and gate it even while presses keep arriving.
            store.Set(key, 1f, KeyProvenance.Digital);

            using var start = new ManualResetEventSlim(false);
            var writer = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 64; i++)
                {
                    store.Set(key, 1f, KeyProvenance.Digital);
                }
            });

            start.Set();
            store.GateHeldKeys();
            await writer;

            // Invariant: after the sweep, pressed writes racing it must not
            // leave the gate clear with a pre-gate press still driving output.
            store.IsGated(key).Should().BeTrue();
            store.Get(key).Value.Should().Be(0f);

            // A pressed write after the race is still swallowed.
            store.Set(key, 1f, KeyProvenance.Digital);
            store.Get(key).Value.Should().Be(0f);

            // Release resets for the next round.
            store.Set(key, 0f, KeyProvenance.Digital);
            store.IsGated(key).Should().BeFalse();
        }
    }
}
