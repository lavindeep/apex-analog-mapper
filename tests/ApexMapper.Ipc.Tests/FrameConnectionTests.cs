using System.Collections.Concurrent;
using ApexMapper.Output.Ipc;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Ipc.Tests;

public class FrameConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Collector
    {
        private readonly ConcurrentQueue<IFrame> _frames = new();
        private readonly int _expected;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Collector(int expected) => _expected = expected;

        public IReadOnlyCollection<IFrame> Frames => _frames;

        public Task Reached => _reached.Task;

        public ValueTask OnFrame(IFrame frame)
        {
            _frames.Enqueue(frame);
            if (Interlocked.Increment(ref _count) >= _expected)
            {
                _reached.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Sends_and_receives_all_frame_types_both_directions()
    {
        await using var pair = await DuplexStreamPair.CreateAsync();
        await using var a = new FrameConnection(pair.EndpointA);
        await using var b = new FrameConnection(pair.EndpointB);

        var atB = new Collector(2);
        var atA = new Collector(2);
        _ = b.RunReadLoopAsync(atB.OnFrame, CancellationToken.None);
        _ = a.RunReadLoopAsync(atA.OnFrame, CancellationToken.None);

        await a.SendAsync(new ControlFrame { SchemaVersion = 1, SequenceNumber = 1, Payload = new PadStatePayload { LeftTrigger = 0.5f } }, CancellationToken.None);
        await a.SendAsync(new HeartbeatFrame { SchemaVersion = 1, SequenceNumber = 2 }, CancellationToken.None);
        await b.SendAsync(new ZeroFrame { SchemaVersion = 1, Reason = "z" }, CancellationToken.None);
        await b.SendAsync(new PanicFrame { SchemaVersion = 1, Reason = "p" }, CancellationToken.None);

        await Task.WhenAll(atB.Reached, atA.Reached).WaitAsync(Timeout);

        atB.Frames.Should().ContainItemsAssignableTo<ControlFrame>();
        atB.Frames.Should().ContainItemsAssignableTo<HeartbeatFrame>();
        atA.Frames.Should().ContainItemsAssignableTo<ZeroFrame>();
        atA.Frames.Should().ContainItemsAssignableTo<PanicFrame>();
    }

    [Fact]
    public async Task Stamps_schema_version_on_send()
    {
        await using var pair = await DuplexStreamPair.CreateAsync();
        await using var a = new FrameConnection(pair.EndpointA);
        await using var b = new FrameConnection(pair.EndpointB);

        var collector = new Collector(1);
        _ = b.RunReadLoopAsync(collector.OnFrame, CancellationToken.None);

        // Sender leaves SchemaVersion unstamped (0); the connection must stamp it.
        await a.SendAsync(new HeartbeatFrame { SequenceNumber = 9 }, CancellationToken.None);
        await collector.Reached.WaitAsync(Timeout);

        collector.Frames.Should().ContainSingle()
            .Which.SchemaVersion.Should().Be(IFrame.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Concurrent_senders_do_not_corrupt_framing()
    {
        await using var pair = await DuplexStreamPair.CreateAsync();
        await using var a = new FrameConnection(pair.EndpointA);
        await using var b = new FrameConnection(pair.EndpointB);

        const int count = 20;
        var collector = new Collector(count);
        _ = b.RunReadLoopAsync(collector.OnFrame, CancellationToken.None);

        var sends = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => a.SendAsync(new HeartbeatFrame { SchemaVersion = 1, SequenceNumber = i }, CancellationToken.None)));
        await Task.WhenAll(sends).WaitAsync(Timeout);

        await collector.Reached.WaitAsync(Timeout);
        collector.Frames.Should().HaveCount(count).And.AllBeOfType<HeartbeatFrame>();
        collector.Frames.OfType<HeartbeatFrame>().Select(f => f.SequenceNumber)
            .Should().BeEquivalentTo(Enumerable.Range(0, count).Select(i => (long)i));
    }

    [Fact]
    public async Task Midstream_garbage_faults_exactly_once_with_protocol_exception()
    {
        await using var pair = await DuplexStreamPair.CreateAsync();
        await using var a = new FrameConnection(pair.EndpointA);

        var faults = new ConcurrentQueue<Exception>();
        a.Faulted += ex => faults.Enqueue(ex);
        _ = a.RunReadLoopAsync(_ => ValueTask.CompletedTask, CancellationToken.None);

        // Feed a's reader a length prefix over the cap: a corrupt/hostile framing.
        byte[] hostilePrefix = [0x00, 0x00, 0x00, 0xFF];
        await pair.EndpointB.WriteAsync(hostilePrefix);
        await pair.EndpointB.FlushAsync();

        await WaitUntilAsync(() => a.IsFaulted);
        faults.Should().ContainSingle().Which.Should().BeOfType<FrameProtocolException>();
        a.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task Send_after_fault_throws_invalid_operation()
    {
        await using var pair = await DuplexStreamPair.CreateAsync();
        await using var a = new FrameConnection(pair.EndpointA);
        _ = a.RunReadLoopAsync(_ => ValueTask.CompletedTask, CancellationToken.None);

        byte[] hostilePrefix = [0x00, 0x00, 0x00, 0xFF];
        await pair.EndpointB.WriteAsync(hostilePrefix);
        await pair.EndpointB.FlushAsync();
        await WaitUntilAsync(() => a.IsFaulted);

        Func<Task> act = async () => await a.SendAsync(new HeartbeatFrame { SchemaVersion = 1 }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Dispose_during_active_read_loop_completes_without_unobserved_exception()
    {
        var pair = await DuplexStreamPair.CreateAsync();
        var a = new FrameConnection(pair.EndpointA);

        var faulted = false;
        a.Faulted += _ => faulted = true;
        var loop = a.RunReadLoopAsync(_ => ValueTask.CompletedTask, CancellationToken.None);

        await a.DisposeAsync();
        await loop.WaitAsync(Timeout);

        loop.IsCompletedSuccessfully.Should().BeTrue();
        faulted.Should().BeFalse();
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_version_frame_is_dropped_counted_and_loop_continues()
    {
        await using var pair = await DuplexStreamPair.CreateAsync();
        await using var a = new FrameConnection(pair.EndpointA);

        var collector = new Collector(1);
        _ = a.RunReadLoopAsync(collector.OnFrame, CancellationToken.None);

        var rawCodec = new FrameCodec();
        // A future-version frame the peer sent, then a valid current-version frame.
        await rawCodec.WriteFrameAsync(pair.EndpointB, new HeartbeatFrame { SchemaVersion = (byte)(IFrame.CurrentSchemaVersion + 1), SequenceNumber = 1 }, CancellationToken.None);
        await rawCodec.WriteFrameAsync(pair.EndpointB, new HeartbeatFrame { SchemaVersion = IFrame.CurrentSchemaVersion, SequenceNumber = 2 }, CancellationToken.None);

        await collector.Reached.WaitAsync(Timeout);
        collector.Frames.Should().ContainSingle()
            .Which.Should().BeOfType<HeartbeatFrame>()
            .Which.SequenceNumber.Should().Be(2);
        a.UnknownVersionFrames.Should().Be(1);
        a.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_after_frame_write_begins_leaves_no_partial_frame()
    {
        // A stream that cancels the send token the instant the first write (the
        // length prefix) lands on the wire, then lets subsequent writes through.
        using var captured = new MemoryStream();
        using var cts = new CancellationTokenSource();
        await using var stream = new CancelOnFirstWriteStream(captured, cts);
        await using var connection = new FrameConnection(stream);

        try
        {
            await connection.SendAsync(
                new HeartbeatFrame { SchemaVersion = 1, SequenceNumber = 42 }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an acceptable outcome — but only if it did not tear a
            // half-written frame onto the wire (asserted below).
        }

        // Contract: a started frame write is atomic. Either the whole frame reached
        // the wire, or the connection faulted — never a partial frame on a healthy
        // connection.
        captured.Position = 0;
        var codec = new FrameCodec();
        if (connection.IsFaulted)
        {
            return;
        }

        IFrame? readBack = await codec.ReadFrameAsync(captured, CancellationToken.None);
        readBack.Should().BeOfType<HeartbeatFrame>()
            .Which.SequenceNumber.Should().Be(42);
    }

    [Fact]
    public async Task Dispose_with_a_queued_sender_never_wedges_and_surfaces_the_transport_error()
    {
        await using var stream = new GatedFailingStream();
        var connection = new FrameConnection(stream);

        // Sender 1 acquires the write lock and parks inside the stream write.
        Task sender1 = Task.Run(() => connection.SendAsync(
            new HeartbeatFrame { SchemaVersion = 1, SequenceNumber = 1 }, CancellationToken.None));
        await stream.FirstWriteEntered.WaitAsync(Timeout);

        // Sender 2 queues on the write lock behind sender 1.
        Task sender2 = Task.Run(() => connection.SendAsync(
            new HeartbeatFrame { SchemaVersion = 1, SequenceNumber = 2 }, CancellationToken.None));
        await Task.Delay(100);

        await connection.DisposeAsync();
        stream.ReleaseWithTransportFailure();

        // Neither sender may wedge; both must complete within the timeout.
        Exception? error1 = await CaptureAsync(sender1).WaitAsync(TimeSpan.FromSeconds(2));
        await CaptureAsync(sender2).WaitAsync(TimeSpan.FromSeconds(2));

        // Sender 1 must surface the real transport failure, not a masking
        // ObjectDisposedException from a disposed lock.
        error1.Should().BeOfType<IOException>();
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class GatedFailingStream : Stream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public Task FirstWriteEntered => _entered.Task;

        public void ReleaseWithTransportFailure() => _release.TrySetResult();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw new IOException("transport failure");
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelOnFirstWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationTokenSource _cancelAfterFirstWrite;
        private int _writes;

        public CancelOnFirstWriteStream(Stream inner, CancellationTokenSource cancelAfterFirstWrite)
        {
            _inner = inner;
            _cancelAfterFirstWrite = cancelAfterFirstWrite;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _inner.WriteAsync(buffer, cancellationToken);
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _cancelAfterFirstWrite.Cancel();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.FlushAsync(cancellationToken);
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
}
