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

    // Mirrors the real supervisor pipe configuration, including CurrentUserOnly.
    private static NamedPipeServerStream StartServer(string sessionId) => new(
        PipeNames.ForSession(sessionId),
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
    public async Task Connects_to_a_current_user_only_server()
    {
        // The supervisor's pipe is created with PipeOptions.CurrentUserOnly so a
        // foreign local user can neither drive nor read the pad channel; the
        // client must carry the same option or the two sides do not pair (on
        // Unix the option changes where the underlying socket lives, and on
        // Windows it adds owner checks on both ends).
        var sessionId = NewSessionId();
        await using var server = new NamedPipeServerStream(
            PipeNames.ForSession(sessionId),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = await ConnectAsync(sessionId, server);

        client.IsConnected.Should().BeTrue();
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
        var control = (ControlFrame)(await codec.ReadFrameAsync(server, CancellationToken.None).AsTask().WaitAsync(Timeout))!;
        var heartbeat = (HeartbeatFrame)(await codec.ReadFrameAsync(server, CancellationToken.None).AsTask().WaitAsync(Timeout))!;
        var zero = (ZeroFrame)(await codec.ReadFrameAsync(server, CancellationToken.None).AsTask().WaitAsync(Timeout))!;
        var panic = (PanicFrame)(await codec.ReadFrameAsync(server, CancellationToken.None).AsTask().WaitAsync(Timeout))!;

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
            var frame = await codec.ReadFrameAsync(server, CancellationToken.None).AsTask().WaitAsync(Timeout);
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
    public async Task Reconnects_on_the_same_instance_after_the_server_drops()
    {
        var sessionId = NewSessionId();
        var server1 = StartServer(sessionId);
        var client = new SupervisorClient(sessionId, Timeout);

        var disconnects = 0;
        var firstDrop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ =>
        {
            Interlocked.Increment(ref disconnects);
            firstDrop.TrySetResult();
        };

        var accept1 = server1.WaitForConnectionAsync();
        await client.ConnectAsync(CancellationToken.None);
        await accept1.WaitAsync(Timeout);
        client.IsConnected.Should().BeTrue();

        await server1.DisposeAsync();
        await firstDrop.Task.WaitAsync(Timeout);

        await using var server2 = StartServer(sessionId);
        var accept2 = server2.WaitForConnectionAsync();
        await client.ConnectAsync(CancellationToken.None);
        await accept2.WaitAsync(Timeout);

        client.IsConnected.Should().BeTrue();
        Volatile.Read(ref disconnects).Should().Be(1);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_disposes_the_old_connection_and_a_stale_drop_leaves_the_new_session_intact()
    {
        var handedOut = new List<ProbeStream>();
        Task<Stream> Connect(CancellationToken _)
        {
            var probe = new ProbeStream();
            lock (handedOut)
            {
                handedOut.Add(probe);
            }

            return Task.FromResult<Stream>(probe);
        }

        await using var client = new SupervisorClient(Connect);
        var disconnects = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnects);

        await client.ConnectAsync(CancellationToken.None);
        client.IsConnected.Should().BeTrue();
        ProbeStream stream1 = handedOut[0];

        // Break the first session through the send path; its read loop stays parked
        // (ProbeStream reads ignore cancellation) so its disconnect continuation is
        // deferred past the reconnect below.
        stream1.FailWrites();
        Func<Task> send = async () => await client.SubmitHeartbeatAsync(CancellationToken.None);
        await send.Should().ThrowAsync<IOException>();

        await WaitUntilAsync(() => !client.IsConnected);
        Volatile.Read(ref disconnects).Should().Be(1);
        // The superseded connection (and its transport) must be disposed — no leak.
        await WaitUntilAsync(() => stream1.Disposed);

        // Reconnect on the same instance: a fresh, live session.
        await client.ConnectAsync(CancellationToken.None);
        client.IsConnected.Should().BeTrue();
        ProbeStream stream2 = handedOut[1];

        // Now let the OLD session's read loop finish. Its stale disconnect must not
        // touch the new session.
        stream1.CompleteReadWithEof();
        await Task.Delay(200);

        client.IsConnected.Should().BeTrue();
        Volatile.Read(ref disconnects).Should().Be(1);
        stream2.Disposed.Should().BeFalse();

        // Let the new session's read loop complete so it does not park indefinitely.
        stream2.CompleteReadWithEof();
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A controllable transport: reads park on a gate (ignoring cancellation, so a
    /// disconnect continuation can be deferred on demand), writes optionally fail
    /// with a transport error, and disposal is observable.
    /// </summary>
    private sealed class ProbeStream : Stream
    {
        private readonly TaskCompletionSource<int> _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _failWrites;

        public bool Disposed { get; private set; }

        public void FailWrites() => _failWrites = true;

        public void CompleteReadWithEof() => _readGate.TrySetResult(0);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => await _readGate.Task.ConfigureAwait(false);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _failWrites
                ? ValueTask.FromException(new IOException("transport failure"))
                : ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
