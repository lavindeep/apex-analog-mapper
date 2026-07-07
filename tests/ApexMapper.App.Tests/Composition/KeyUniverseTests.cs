using ApexMapper.App.Composition;
using ApexMapper.Core.Keys;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Composition;

public sealed class KeyUniverseTests
{
    [Fact]
    public void Full_index_covers_all_three_scan_code_pages()
    {
        var index = KeyUniverse.CreateFullIndex();

        index.Count.Should().Be(3 * 0xFF, "plain, E0- and E1-prefixed pages, 0x01..0xFF each");
    }

    [Theory]
    [InlineData((ushort)0x0011)] // W — plain page
    [InlineData((ushort)0x00FF)] // top of the plain page
    [InlineData((ushort)0xE01D)] // Right Ctrl — E0 page
    [InlineData((ushort)0xE11D)] // Pause lead-in — E1 page
    public void Every_decoder_emittable_code_has_a_slot(ushort scanCode)
    {
        var index = KeyUniverse.CreateFullIndex();

        index.TryGetSlot(KeyId.FromScanCode(scanCode), out _).Should().BeTrue();
    }

    [Fact]
    public void Bare_prefix_bytes_are_not_keys()
    {
        var index = KeyUniverse.CreateFullIndex();

        index.TryGetSlot(KeyId.FromScanCode(0x0000), out _).Should().BeFalse();
        index.TryGetSlot(KeyId.FromScanCode(0xE000), out _).Should().BeFalse();
        index.TryGetSlot(KeyId.FromScanCode(0xE100), out _).Should().BeFalse();
    }
}
