using ApexMapper.Output.Ipc;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Ipc.Tests;

/// <summary>
/// Freezes the v1 wire layout. These frames are serialized with fixed field
/// values and the exact on-wire bytes (length prefix + MessagePack payload) are
/// pinned. Any change that alters the format — a reordered [Key], a new field, a
/// resolver swap — breaks these tests on purpose, forcing a deliberate schema
/// bump rather than a silent, incompatible change.
/// </summary>
public class WireCompatibilityTests
{
    private static readonly FrameCodec Codec = new();

    private static async Task<string> ToWireHexAsync(IFrame frame)
    {
        using var ms = new MemoryStream();
        await Codec.WriteFrameAsync(ms, frame, CancellationToken.None);
        return Convert.ToHexString(ms.ToArray());
    }

    [Fact]
    public async Task ControlFrame_v1_wire_bytes_are_frozen()
    {
        var frame = new ControlFrame
        {
            SchemaVersion = 1,
            SequenceNumber = 7,
            TimestampTicks = 638_400_000_000_000_000L,
            Payload = new PadStatePayload
            {
                LeftStickX = 0.5f,
                RightTrigger = 1.0f,
                ButtonA = true,
                DpadUp = true,
            },
        };

        var hex = await ToWireHexAsync(frame);

        hex.Should().Be(
            "3E0000009200940107CF08DC0D6AE89C0000DC0015CA3F000000CA00000000CA00000000" +
            "CA00000000CA00000000CA3F800000C3C2C2C2C2C2C2C2C2C2C2C3C2C2C2");
    }

    [Fact]
    public async Task HeartbeatFrame_v1_wire_bytes_are_frozen()
    {
        var frame = new HeartbeatFrame
        {
            SchemaVersion = 1,
            SequenceNumber = 99,
            TimestampTicks = 638_400_000_000_000_000L,
        };

        var hex = await ToWireHexAsync(frame);

        hex.Should().Be("0E00000092019301CF08DC0D6AE89C000063");
    }
}
