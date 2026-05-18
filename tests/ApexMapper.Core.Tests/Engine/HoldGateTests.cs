using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Engine;

public class HoldGateTests
{
    private static readonly KeyId W = KeyId.FromScanCode(0x11);
    private static readonly KeyId S = KeyId.FromScanCode(0x1F);

    [Fact]
    public void Newly_constructed_gate_blocks_nothing()
    {
        var gate = new HoldGate();
        gate.IsIgnored(W).Should().BeFalse();
    }

    [Fact]
    public void GateHeldKeys_marks_provided_keys_as_ignored()
    {
        var gate = new HoldGate();
        gate.GateHeldKeys(new[] { W, S });
        gate.IsIgnored(W).Should().BeTrue();
        gate.IsIgnored(S).Should().BeTrue();
    }

    [Fact]
    public void NotifyKeyReleased_clears_a_gated_key()
    {
        var gate = new HoldGate();
        gate.GateHeldKeys(new[] { W });
        gate.NotifyKeyReleased(W);
        gate.IsIgnored(W).Should().BeFalse();
    }

    [Fact]
    public void Releasing_then_repressing_does_not_re_gate()
    {
        var gate = new HoldGate();
        gate.GateHeldKeys(new[] { W });
        gate.NotifyKeyReleased(W);
        gate.IsIgnored(W).Should().BeFalse();
    }

    [Fact]
    public void GateAll_with_empty_list_is_noop()
    {
        var gate = new HoldGate();
        gate.GateHeldKeys(Array.Empty<KeyId>());
        gate.IsIgnored(W).Should().BeFalse();
    }
}
