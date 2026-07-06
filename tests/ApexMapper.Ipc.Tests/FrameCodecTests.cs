using System.Buffers.Binary;
using ApexMapper.Output.Ipc;
using FluentAssertions;
using MessagePack;
using Xunit;

namespace ApexMapper.Ipc.Tests;

public class FrameCodecTests
{
    private readonly FrameCodec _codec = new();

    private static ControlFrame SampleControl() => new()
    {
        SchemaVersion = IFrame.CurrentSchemaVersion,
        SequenceNumber = 42,
        TimestampTicks = 638_000_000_000_000_000L,
        Payload = new PadStatePayload
        {
            LeftStickX = 0.25f,
            LeftTrigger = 1.0f,
            ButtonA = true,
            DpadLeft = true,
        },
    };

    private static async Task<MemoryStream> WrittenAsync(FrameCodec codec, IFrame frame)
    {
        var ms = new MemoryStream();
        await codec.WriteFrameAsync(ms, frame, CancellationToken.None);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task RoundTrips_control_frame_with_full_payload()
    {
        var original = SampleControl();
        using var ms = await WrittenAsync(_codec, original);

        var read = await _codec.ReadFrameAsync(ms, CancellationToken.None);

        read.Should().BeOfType<ControlFrame>().And.Be(original);
    }

    [Fact]
    public async Task RoundTrips_heartbeat_frame()
    {
        var original = new HeartbeatFrame
        {
            SchemaVersion = IFrame.CurrentSchemaVersion,
            TimestampTicks = 123,
            SequenceNumber = 7,
        };
        using var ms = await WrittenAsync(_codec, original);

        var read = await _codec.ReadFrameAsync(ms, CancellationToken.None);

        read.Should().BeOfType<HeartbeatFrame>().And.Be(original);
    }

    [Fact]
    public async Task RoundTrips_zero_frame()
    {
        var original = new ZeroFrame
        {
            SchemaVersion = IFrame.CurrentSchemaVersion,
            TimestampTicks = 555,
            Reason = "heartbeat gap",
        };
        using var ms = await WrittenAsync(_codec, original);

        var read = await _codec.ReadFrameAsync(ms, CancellationToken.None);

        read.Should().BeOfType<ZeroFrame>().And.Be(original);
    }

    [Fact]
    public async Task RoundTrips_panic_frame()
    {
        var original = new PanicFrame
        {
            SchemaVersion = IFrame.CurrentSchemaVersion,
            TimestampTicks = 999,
            Reason = "user panic",
        };
        using var ms = await WrittenAsync(_codec, original);

        var read = await _codec.ReadFrameAsync(ms, CancellationToken.None);

        read.Should().BeOfType<PanicFrame>().And.Be(original);
    }

    [Fact]
    public async Task CleanEof_at_frame_boundary_returns_null()
    {
        using var empty = new MemoryStream();

        var read = await _codec.ReadFrameAsync(empty, CancellationToken.None);

        read.Should().BeNull();
    }

    [Fact]
    public async Task CleanEof_after_a_complete_frame_returns_null_on_next_read()
    {
        using var ms = await WrittenAsync(_codec, SampleControl());

        var first = await _codec.ReadFrameAsync(ms, CancellationToken.None);
        var second = await _codec.ReadFrameAsync(ms, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task Truncated_length_prefix_throws_protocol_exception()
    {
        using var ms = new MemoryStream(new byte[] { 0x01, 0x02 });

        Func<Task> act = async () => await _codec.ReadFrameAsync(ms, CancellationToken.None);

        await act.Should().ThrowAsync<FrameProtocolException>();
    }

    [Fact]
    public async Task Truncated_body_throws_protocol_exception()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 100);
        using var ms = new MemoryStream([.. prefix, 0x01, 0x02, 0x03]);

        Func<Task> act = async () => await _codec.ReadFrameAsync(ms, CancellationToken.None);

        await act.Should().ThrowAsync<FrameProtocolException>();
    }

    [Fact]
    public async Task Zero_length_prefix_throws_protocol_exception()
    {
        using var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        Func<Task> act = async () => await _codec.ReadFrameAsync(ms, CancellationToken.None);

        await act.Should().ThrowAsync<FrameProtocolException>();
    }

    [Fact]
    public async Task Oversize_length_prefix_throws_protocol_exception()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)FrameCodec.MaxFrameBytes + 1);
        using var ms = new MemoryStream(prefix);

        Func<Task> act = async () => await _codec.ReadFrameAsync(ms, CancellationToken.None);

        await act.Should().ThrowAsync<FrameProtocolException>();
    }

    [Fact]
    public async Task Garbage_body_surfaces_as_protocol_exception_wrapping_serialization_error()
    {
        // 0xC1 is the MessagePack "never used" byte and forces a deserialize error.
        byte[] garbage = [0xC1, 0xC1, 0xC1, 0xC1, 0xC1];
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)garbage.Length);
        using var ms = new MemoryStream([.. prefix, .. garbage]);

        var ex = await Assert.ThrowsAsync<FrameProtocolException>(
            async () => await _codec.ReadFrameAsync(ms, CancellationToken.None));

        ex.InnerException.Should().BeOfType<MessagePackSerializationException>();
    }

    [Fact]
    public async Task Writing_an_unstamped_frame_throws()
    {
        using var ms = new MemoryStream();
        var unstamped = new HeartbeatFrame { SchemaVersion = 0, TimestampTicks = 1, SequenceNumber = 1 };

        Func<Task> act = async () => await _codec.WriteFrameAsync(ms, unstamped, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_during_read_propagates()
    {
        using var ms = await WrittenAsync(_codec, SampleControl());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await _codec.ReadFrameAsync(ms, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void IsKnownVersion_accepts_current_and_rejects_zero_and_future()
    {
        FrameCodec.IsKnownVersion(new HeartbeatFrame { SchemaVersion = IFrame.CurrentSchemaVersion }).Should().BeTrue();
        FrameCodec.IsKnownVersion(new HeartbeatFrame { SchemaVersion = 0 }).Should().BeFalse();
        FrameCodec.IsKnownVersion(new HeartbeatFrame { SchemaVersion = IFrame.CurrentSchemaVersion + 1 }).Should().BeFalse();
    }
}
