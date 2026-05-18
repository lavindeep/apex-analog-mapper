using ApexMapper.Core.Keys;

namespace ApexMapper.Input.Abstractions.Tests.Keys;

public class KeyIndexTests
{
    [Fact]
    public void Ctor_from_enumerable_sets_Count_to_distinct_key_count()
    {
        var index = new KeyIndex(new[] { KeyId.FromScanCode(0x11), KeyId.FromScanCode(0x1E) });
        index.Count.Should().Be(2);
    }

    [Fact]
    public void TryGetSlot_returns_true_and_slot_in_range_for_known_key()
    {
        var a = KeyId.FromScanCode(0x11);
        var b = KeyId.FromScanCode(0x1E);
        var index = new KeyIndex(new[] { a, b });

        index.TryGetSlot(a, out var slotA).Should().BeTrue();
        index.TryGetSlot(b, out var slotB).Should().BeTrue();

        slotA.Should().BeInRange(0, index.Count - 1);
        slotB.Should().BeInRange(0, index.Count - 1);
    }

    [Fact]
    public void TryGetSlot_returns_false_with_minus_one_for_unknown_key()
    {
        var index = new KeyIndex(new[] { KeyId.FromScanCode(0x11) });

        index.TryGetSlot(KeyId.FromScanCode(0x42), out var slot).Should().BeFalse();
        slot.Should().Be(-1);
    }

    [Fact]
    public void Slots_are_unique_and_dense_zero_to_count_minus_one()
    {
        var keys = new[]
        {
            KeyId.FromScanCode(0x11),
            KeyId.FromScanCode(0x1E),
            KeyId.FromScanCode(0x20),
            KeyId.FromScanCode(0xE04D),
            KeyId.FromScanCode(0xE11D),
        };
        var index = new KeyIndex(keys);

        var assignedSlots = new HashSet<int>();
        foreach (var key in keys)
        {
            index.TryGetSlot(key, out var slot).Should().BeTrue();
            assignedSlots.Add(slot).Should().BeTrue("each key gets a unique slot");
        }

        assignedSlots.Should().BeEquivalentTo(Enumerable.Range(0, keys.Length));
    }

    [Fact]
    public void Empty_key_set_has_zero_count_and_no_lookups_succeed()
    {
        var index = new KeyIndex(Enumerable.Empty<KeyId>());

        index.Count.Should().Be(0);
        index.TryGetSlot(KeyId.FromScanCode(0x11), out var slot).Should().BeFalse();
        slot.Should().Be(-1);
    }

    [Fact]
    public void Duplicate_keys_collapse_to_a_single_slot()
    {
        var key = KeyId.FromScanCode(0x11);
        var index = new KeyIndex(new[] { key, key });

        index.Count.Should().Be(1);
        index.TryGetSlot(key, out var slot).Should().BeTrue();
        slot.Should().Be(0);
    }

    [Fact]
    public void KeyAt_returns_KeyId_registered_at_slot()
    {
        var a = KeyId.FromScanCode(0x11);
        var b = KeyId.FromScanCode(0x1E);
        var index = new KeyIndex(new[] { a, b });

        index.TryGetSlot(a, out var slotA).Should().BeTrue();
        index.TryGetSlot(b, out var slotB).Should().BeTrue();

        index.KeyAt(slotA).Should().Be(a);
        index.KeyAt(slotB).Should().Be(b);
    }

    [Fact]
    public void KeyAt_throws_when_slot_is_negative()
    {
        var index = new KeyIndex(new[] { KeyId.FromScanCode(0x11) });
        var act = () => index.KeyAt(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void KeyAt_throws_when_slot_is_at_or_above_count()
    {
        var index = new KeyIndex(new[] { KeyId.FromScanCode(0x11) });
        var act = () => index.KeyAt(index.Count);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Keys_property_returns_all_registered_KeyIds()
    {
        var a = KeyId.FromScanCode(0x11);
        var b = KeyId.FromScanCode(0x1E);
        var c = KeyId.FromScanCode(0xE04D);
        var index = new KeyIndex(new[] { a, b, c });

        index.Keys.Should().BeEquivalentTo(new[] { a, b, c });
    }

    [Fact]
    public void Keys_property_is_indexable_by_slot()
    {
        var a = KeyId.FromScanCode(0x11);
        var b = KeyId.FromScanCode(0x1E);
        var index = new KeyIndex(new[] { a, b });

        for (var i = 0; i < index.Count; i++)
        {
            index.Keys[i].Should().Be(index.KeyAt(i));
        }
    }

    [Fact]
    public void Ctor_throws_when_keys_argument_is_null()
    {
        var act = () => new KeyIndex(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
