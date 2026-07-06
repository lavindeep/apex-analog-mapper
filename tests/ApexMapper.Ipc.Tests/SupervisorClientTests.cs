using System.Diagnostics;
using System.IO.Pipes;
using ApexMapper.Output.Ipc;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Ipc.Tests;

public class SupervisorClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public StubTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // Short session id: the Unix domain-socket path backing the pipe has a 104-char cap.
    private static string NewSessionId() => "s" + Guid.NewGuid().ToString("N")[..8];

    private static NamedPipeServerStream StartServer(string sessionId) => new(
        PipeNames.ForSession(sessionId),
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static async Task<SupervisorClient> ConnectAsync(
        string sessionId, NamedPipeServerStream server, TimeProvider? time = null)
    {
        var accept = server.WaitForConnectionAsync();
        var client = new SupervisorClient(sessionId, Timeout, time);
        await client.ConnectAsync(CancellationToken.None);
        await accept.WaitAsync(Timeout);
        return client;
    }

    [Fact]
    public async Task Connects_and_reports_connected()
    {
        var sessionId = NewSessionId();
        await using var server = StartServer(sessionId);
        await using var client = await ConnectAsync(sessionId, server);

        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Submits_each_frame_type_with_stamped_version_sequence_and_timestamp()
    {
        var sessionId = NewSessionId();
        var time = new StubTimeProvider(FixedNow);
        await using var server = StartServer(sessionId);
        await using var client = await ConnectAsync(sessionId, server, time);

        await client.SubmitControlAsync(new PadStatePayload { LeftTrigger = 0.75f, ButtonA = true }, CancellationToken.None);
        await client.SubmitHeartbeatAsync(CancellationToken.None);
        await client.SubmitZeroAsync("gap", CancellationToken.None);
        await client.SubmitPanicAsync("boom", CancellationToken.None);

        var codec = new FrameCodec();
        var control = (ControlFrame)(await codec.ReadFrameAsync(server, CancellationToken.None))!;
        var heartbeat = (HeartbeatFrame)(await codec.ReadFrameAsync(server, CancellationToken.None))!;
        var zero = (ZeroFrame)(await codec.ReadFrameAsync(server, CancellationToken.None))!;
        var panic = (PanicFrame)(await codec.ReadFrameAsync(server, CancellationToken.None))!;

        control.SchemaVersion.Should().Be(IFrame.CurrentSchemaVersion);
        control.SequenceNumber.Should().Be(1);
        control.TimestampTicks.Should().Be(FixedNow.UtcTicks);
        control.Payload!.LeftTrigger.Should().Be(0.75f);
        control.Payload!.ButtonA.Should().BeTrue();

        heartbeat.SchemaVersion.Should().Be(IFrame.CurrentSchemaVersion);
        heartbeat.SequenceNumber.Should().Be(2);
        heartbeat.TimestampTicks.Should().Be(FixedNow.UtcTicks);

        zero.SchemaVersion.Should().Be(IFrame.CurrentSchemaVersion);
        zero.Reason.Should().Be("gap");

        panic.SchemaVersion.Should().Be(IFrame.CurrentSchemaVersion);
        panic.Reason.Should().Be("boom");
    }

    [Fact]
    public async Task Sequence_numbers_strictly_increase_across_mixed_sends()
    {
        var sessionId = NewSessionId();
        await using var server = StartServer(sessionId);
        await using var client = await ConnectAsync(sessionId, server);

        await client.SubmitControlAsync(new PadStatePayload(), CancellationToken.None);
        await client.SubmitHeartbeatAsync(CancellationToken.None);
        await client.SubmitControlAsync(new PadStatePayload(), CancellationToken.None);

        var codec = new FrameCodec();
        var seqs = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var frame = await codec.ReadFrameAsync(server, CancellationToken.None);
            seqs.Add(frame switch
            {
                ControlFrame c => c.SequenceNumber,
                HeartbeatFrame h => h.SequenceNumber,
                _ => throw new InvalidOperationException(),
            });
        }

        seqs.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Connect_timeout_to_nonexistent_pipe_throws_within_budget()
    {
        var client = new SupervisorClient(NewSessionId(), TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();

        Func<Task> act = async () => await client.ConnectAsync(CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        client.IsConnected.Should().BeFalse();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Server_close_midsession_raises_disconnected_and_sends_then_throw()
    {
        var sessionId = NewSessionId();
        var server = StartServer(sessionId);
        await using var client = await ConnectAsync(sessionId, server);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ => disconnected.TrySetResult();

        await server.DisposeAsync();
        await disconnected.Task.WaitAsync(Timeout);

        client.IsConnected.Should().BeFalse();
        Func<Task> act = async () => await client.SubmitHeartbeatAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Double_dispose_is_safe()
    {
        var sessionId = NewSessionId();
        await using var server = StartServer(sessionId);
        var client = await ConnectAsync(sessionId, server);

        await client.DisposeAsync();
        Func<Task> act = async () => await client.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
