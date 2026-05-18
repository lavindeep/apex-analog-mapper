using ApexMapper.Core.Keys;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Keys;

public class KeyStateStoreTests
{
    [Fact]
    public void Get_returns_rest_when_unset()
    {
        var store = new KeyStateStore();
        store.Get(KeyId.FromScanCode(0x11)).Value.Should().Be(0f);
        store.Get(KeyId.FromScanCode(0x11)).Source.Should().Be(KeyProvenance.Digital);
    }

    [Fact]
    public void Set_then_Get_round_trips_value_and_provenance()
    {
        var store = new KeyStateStore();
        store.Set(KeyId.FromScanCode(0x20), 0.42f, KeyProvenance.Analog);
        var state = store.Get(KeyId.FromScanCode(0x20));
        state.Value.Should().BeApproximately(0.42f, 1e-6f);
        state.Source.Should().Be(KeyProvenance.Analog);
    }

    [Fact]
    public void Set_clamps_value_to_unit_interval()
    {
        var store = new KeyStateStore();
        store.Set(KeyId.FromScanCode(0x10), 1.5f, KeyProvenance.Analog);
        store.Get(KeyId.FromScanCode(0x10)).Value.Should().Be(1f);
        store.Set(KeyId.FromScanCode(0x10), -0.2f, KeyProvenance.Analog);
        store.Get(KeyId.FromScanCode(0x10)).Value.Should().Be(0f);
    }

    [Fact]
    public void KeyId_equality_uses_scan_code()
    {
        KeyId.FromScanCode(0x11).Should().Be(KeyId.FromScanCode(0x11));
        KeyId.FromScanCode(0x11).Should().NotBe(KeyId.FromScanCode(0x1F));
    }
}
