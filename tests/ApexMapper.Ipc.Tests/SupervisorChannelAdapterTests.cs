using System.IO.Pipes;
using ApexMapper.Core.Pipeline;
using ApexMapper.Output.Ipc;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Ipc.Tests;

public class SupervisorChannelAdapterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Short session id: the Unix domain-socket path backing the pipe has a 104-char cap.
    private static string NewSessionId() => "a" + Guid.NewGuid().ToString("N")[..8];

    // Mirrors the real supervisor pipe configuration, including CurrentUserOnly and
    // explicit buffer sizes. On Windows the sizes seed the pipe's write quota; the
    // zero-size default lets a client write park until the server reads, which would
    // wedge cadence tests that write several frames before the first server read.
    private static NamedPipeServerStream StartServer(string sessionId) => new(
        PipeNames.ForSession(sessionId),
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: FrameCodec.MaxFrameBytes,
        outBufferSize: FrameCodec.MaxFrameBytes);

    private static async Task<T> ReadFrameAsync<T>(FrameCodec codec, Stream stream)
        where T : class, IFrame
    {
        IFrame? frame = await codec.ReadFrameAsync(stream, CancellationToken.None).AsTask().WaitAsync(Timeout);
        frame.Should().NotBeNull();
        return frame.Should().BeOfType<T>().Subject;
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Deterministic transport source for the adapter's internal client-factory
    /// seam: each connect attempt either fails (per <see cref="FailAttempt"/>)
    /// or hands out a fresh controllable <see cref="FakeStream"/>.
    /// </summary>
    private sealed class FakeConnectHub
    {
        private readonly object _lock = new();
        private readonly List<FakeStream> _streams = new();
        private int _attempts;

        /// <summary>Whether the given 1-based connect attempt should fail.</summary>
        public Func<int, bool> FailAttempt { get; init; } = _ => false;

        public int Attempts
        {
            get
            {
                lock (_lock)
                {
                    return _attempts;
                }
            }
        }

        public FakeStream Stream(int index)
        {
            lock (_lock)
            {
                return _streams[index];
            }
        }

        public int StreamCount
        {
            get
            {
                lock (_lock)
                {
                    return _streams.Count;
                }
            }
        }

        public bool AllStreamsDisposed
        {
            get
            {
                lock (_lock)
                {
                    return _streams.All(s => s.Disposed);
                }
            }
        }

        public SupervisorClient CreateClient(TimeProvider time) => new(ConnectAsync, time);

        private Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int attempt;
            lock (_lock)
            {
                attempt = ++_attempts;
            }

            if (FailAttempt(attempt))
            {
                return Task.FromException<Stream>(new IOException($"connect refused (attempt {attempt})"));
            }

            var stream = new FakeStream();
            lock (_lock)
            {
                _streams.Add(stream);
            }

            return Task.FromResult<Stream>(stream);
        }
    }

    /// <summary>
    /// A controllable transport: reads park on a gate until EOF is signaled (or
    /// the stream is disposed), writes append to a decodable capture — or fail,
    /// or park forever, on demand — and disposal is observable.
    /// </summary>
    private sealed class FakeStream : Stream
    {
        private readonly TaskCompletionSource<int> _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private readonly MemoryStream _written = new();
        private volatile bool _failWrites;
        private volatile bool _parkWrites;
        private volatile bool _disposed;

        public bool Disposed => _disposed;

        /// <summary>Completes once a parked write has actually entered the stream —
        /// i.e. the sender now holds the connection's frame write lock.</summary>
        public Task WriteParked => _writeParked.Task;

        public void FailWrites() => _failWrites = true;

        /// <summary>Makes writes park indefinitely (ignoring cancellation), so a
        /// test can wedge an in-flight send while it holds the frame write lock.</summary>
        public void ParkWrites() => _parkWrites = true;

        public void CompleteReadWithEof() => _readGate.TrySetResult(0);

        public async Task<List<IFrame>> DecodeCompleteFramesAsync()
        {
            byte[] bytes;
            lock (_lock)
            {
                bytes = _written.ToArray();
            }

            var codec = new FrameCodec();
            using var buffer = new MemoryStream(bytes);
            var frames = new List<IFrame>();
            try
            {
                while (await codec.ReadFrameAsync(buffer, CancellationToken.None) is { } frame)
                {
                    frames.Add(frame);
                }
            }
            catch (FrameProtocolException)
            {
                // Trailing partial frame from an in-flight send; complete frames
                // before it still count.
            }

            return frames;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => await _readGate.Task.ConfigureAwait(false);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_parkWrites)
            {
                _writeParked.TrySetResult();
                await _writeGate.Task.ConfigureAwait(false);
            }

            if (_failWrites)
            {
                throw new IOException("transport failure");
            }

            lock (_lock)
            {
                _written.Write(buffer.Span);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            // Let a parked read loop observe EOF so it can unwind.
            _readGate.TrySetResult(0);
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StatusLog
    {
        private readonly object _lock = new();
        private readonly List<(bool Connected, Exception? Error)> _entries = new();

        public void Record(bool connected, Exception? error)
        {
            lock (_lock)
            {
                _entries.Add((connected, error));
            }
        }

        public IReadOnlyList<(bool Connected, Exception? Error)> Snapshot()
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    [Fact]
    public async Task Start_connects_and_raises_connected_status_once()
    {
        var sessionId = NewSessionId();
        await using var server = StartServer(sessionId);
        var accept = server.WaitForConnectionAsync();

        var statuses = new StatusLog();
        await using var adapter = new SupervisorChannelAdapter(sessionId);
        adapter.StatusChanged += statuses.Record;

        adapter.Start();

        await accept.WaitAsync(Timeout);
        await WaitUntilAsync(() => adapter.IsConnected);
        await WaitUntilAsync(() => statuses.Snapshot().Count == 1);
        statuses.Snapshot().Should().Equal((true, null));
    }

    [Fact]
    public async Task Control_and_heartbeat_frames_follow_the_cadence_and_carry_the_latest_state()
    {
        var sessionId = NewSessionId();
        var time = new ManualTimeProvider();
        await using var server = StartServer(sessionId);
        var accept = server.WaitForConnectionAsync();

        await using var adapter = new SupervisorChannelAdapter(sessionId, timeProvider: time);
        adapter.Start();
        await accept.WaitAsync(Timeout);
        await WaitUntilAsync(() => adapter.IsConnected);
        // Both cadence timers must be armed before the clock moves.
        await WaitUntilAsync(() => time.ScheduledTimerCount == 2);

        var codec = new FrameCodec();

        adapter.SetState(new VirtualPadState { LeftTrigger = 0.5f });
        time.Advance(TimeSpan.FromMilliseconds(100));
        var control1 = await ReadFrameAsync<ControlFrame>(codec, server);
        control1.Payload!.LeftTrigger.Should().Be(0.5f);

        // Latest-wins: the slot holds a full state, not a delta.
        adapter.SetState(new VirtualPadState { RightTrigger = 0.25f });
        time.Advance(TimeSpan.FromMilliseconds(100));
        var control2 = await ReadFrameAsync<ControlFrame>(codec, server);
        control2.Payload!.RightTrigger.Should().Be(0.25f);
        control2.Payload!.LeftTrigger.Should().Be(0f);

        // t = 250 ms: the heartbeat cadence fires independently of control.
        time.Advance(TimeSpan.FromMilliseconds(50));
        await ReadFrameAsync<HeartbeatFrame>(codec, server);

        // t = 300 ms: control keeps its own 100 ms grid.
        time.Advance(TimeSpan.FromMilliseconds(50));
        var control3 = await ReadFrameAsync<ControlFrame>(codec, server);
        control3.Payload!.RightTrigger.Should().Be(0.25f);
    }

    [Fact]
    public async Task State_set_before_connect_is_carried_by_the_first_control_frame()
    {
        var sessionId = NewSessionId();
        var time = new ManualTimeProvider();
        await using var server = StartServer(sessionId);
        var accept = server.WaitForConnectionAsync();

        await using var adapter = new SupervisorChannelAdapter(sessionId, timeProvider: time);
        // The mapping loop may already be pushing states while the channel is
        // still connecting; the slot must simply hold the latest.
        adapter.SetState(new VirtualPadState { LeftStickX = -1f });

        adapter.Start();
        await accept.WaitAsync(Timeout);
        await WaitUntilAsync(() => time.ScheduledTimerCount == 2);

        time.Advance(TimeSpan.FromMilliseconds(100));
        var control = await ReadFrameAsync<ControlFrame>(new FrameCodec(), server);
        control.Payload!.LeftStickX.Should().Be(-1f);
    }

    [Fact]
    public async Task Send_failure_triggers_reconnect_and_cadence_resumes_without_escaping()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub();
        var statuses = new StatusLog();
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);
        adapter.StatusChanged += statuses.Record;
        adapter.SetState(new VirtualPadState { LeftTrigger = 1f });

        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected && time.ScheduledTimerCount == 2);

        // Break the live session through the send path: the failed control send
        // must be contained, tear the session down, and drive a reconnect.
        hub.Stream(0).FailWrites();
        time.Advance(TimeSpan.FromMilliseconds(100));

        await WaitUntilAsync(() => statuses.Snapshot().Count == 3);
        statuses.Snapshot().Select(s => s.Connected).Should().Equal(true, false, true);
        statuses.Snapshot()[1].Error.Should().BeOfType<IOException>();
        adapter.IsConnected.Should().BeTrue();
        hub.Attempts.Should().Be(2);

        // Cadence resumes on the fresh session.
        await WaitUntilAsync(() => time.ScheduledTimerCount == 2);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(async () =>
            (await hub.Stream(1).DecodeCompleteFramesAsync()).OfType<ControlFrame>().Any());
        var control = (await hub.Stream(1).DecodeCompleteFramesAsync()).OfType<ControlFrame>().First();
        control.Payload!.LeftTrigger.Should().Be(1f);
    }

    [Fact]
    public async Task Initial_connect_retries_with_doubling_backoff_until_the_supervisor_appears()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub { FailAttempt = n => n <= 2 };
        var statuses = new StatusLog();
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);
        adapter.StatusChanged += statuses.Record;

        adapter.Start();

        // First attempt is immediate; its failure arms the initial 250 ms delay.
        await WaitUntilAsync(() => hub.Attempts == 1);
        await WaitUntilAsync(() => time.ScheduledTimerCount == 1);
        time.Advance(TimeSpan.FromMilliseconds(249));
        await Task.Delay(50);
        hub.Attempts.Should().Be(1, "the retry must not fire before the initial delay elapses");
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => hub.Attempts == 2);

        // Second failure doubles the delay to 500 ms.
        await WaitUntilAsync(() => time.ScheduledTimerCount == 1);
        time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Delay(50);
        hub.Attempts.Should().Be(2, "the retry must not fire before the doubled delay elapses");
        time.Advance(TimeSpan.FromMilliseconds(1));

        await WaitUntilAsync(() => adapter.IsConnected);
        hub.Attempts.Should().Be(3);
        statuses.Snapshot().Should().Equal((true, null));
    }

    [Fact]
    public async Task Reconnect_backoff_caps_at_the_configured_maximum()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub { FailAttempt = _ => true };
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);

        adapter.Start();
        await WaitUntilAsync(() => hub.Attempts == 1);

        // Doubling ladder from the defaults: 250, 500, 1000, 2000, then capped at 2000.
        var expectedDelays = new[] { 250, 500, 1000, 2000, 2000 };
        var attempts = 1;
        foreach (var delayMs in expectedDelays)
        {
            await WaitUntilAsync(() => time.ScheduledTimerCount == 1);
            time.Advance(TimeSpan.FromMilliseconds(delayMs - 1));
            await Task.Delay(50);
            hub.Attempts.Should().Be(attempts, $"no retry may fire before the full {delayMs} ms delay");
            time.Advance(TimeSpan.FromMilliseconds(1));
            attempts++;
            await WaitUntilAsync(() => hub.Attempts == attempts);
        }
    }

    [Fact]
    public async Task Backoff_restarts_at_the_initial_delay_after_a_successful_session()
    {
        var time = new ManualTimeProvider();
        // Fail attempts 1 and 2 so the ladder escalates 250 -> 500 before a session
        // finally connects on attempt 3; fail attempt 4 too so the reconnect that
        // follows the drop arms a measurable backoff delay.
        var hub = new FakeConnectHub { FailAttempt = n => n <= 2 || n == 4 };
        var statuses = new StatusLog();
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);
        adapter.StatusChanged += statuses.Record;

        adapter.Start();

        // Attempt 1 fails immediately, arming the initial 250 ms delay.
        await WaitUntilAsync(() => hub.Attempts == 1);
        await WaitUntilAsync(() => time.ScheduledTimerCount == 1);
        time.Advance(TimeSpan.FromMilliseconds(250));

        // Attempt 2 fails, doubling the delay to 500 ms.
        await WaitUntilAsync(() => hub.Attempts == 2);
        await WaitUntilAsync(() => time.ScheduledTimerCount == 1);
        time.Advance(TimeSpan.FromMilliseconds(500));

        // Attempt 3 connects: the ladder is now escalated (its next rung is 1000 ms).
        await WaitUntilAsync(() => adapter.IsConnected);
        hub.Attempts.Should().Be(3);

        // Drop the live session. The reconnect starts a fresh driver run, whose
        // ladder must restart at the initial 250 ms — not the escalated rung.
        hub.Stream(0).CompleteReadWithEof();
        await WaitUntilAsync(() => hub.Attempts == 4);
        await WaitUntilAsync(() => time.ScheduledTimerCount == 1);

        // Just short of the initial delay: the retry must not fire yet.
        time.Advance(TimeSpan.FromMilliseconds(249));
        await Task.Delay(50);
        hub.Attempts.Should().Be(4, "a fresh reconnect cycle must restart at the initial 250 ms delay, not an escalated one");

        // The remaining 1 ms completes the initial delay and fires the next attempt.
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => adapter.IsConnected);
        hub.Attempts.Should().Be(5);
        statuses.Snapshot().Select(s => s.Connected).Should().Equal(true, false, true);
        statuses.Snapshot()[1].Error.Should().BeNull("a clean peer close carries no fault");
    }

    [Fact]
    public async Task Panic_sends_the_panic_frame_then_disconnects_without_auto_reconnect()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub();
        var statuses = new StatusLog();
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);
        adapter.StatusChanged += statuses.Record;

        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected && time.ScheduledTimerCount == 2);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(async () =>
            (await hub.Stream(0).DecodeCompleteFramesAsync()).OfType<ControlFrame>().Any());

        await adapter.SubmitPanicAsync("user hotkey", CancellationToken.None).WaitAsync(Timeout);

        var frames = await hub.Stream(0).DecodeCompleteFramesAsync();
        var panic = frames[^1].Should().BeOfType<PanicFrame>().Subject;
        panic.Reason.Should().Be("user hotkey");

        adapter.IsConnected.Should().BeFalse();
        await WaitUntilAsync(() => statuses.Snapshot().Count == 2);
        statuses.Snapshot().Select(s => s.Connected).Should().Equal(true, false);
        await WaitUntilAsync(() => hub.Stream(0).Disposed);

        // Panic means the user forced the pad off: no reconnect, no more frames,
        // no matter how much time passes.
        var framesAfterPanic = frames.Count;
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
        }

        await Task.Delay(50);
        hub.Attempts.Should().Be(1);
        (await hub.Stream(0).DecodeCompleteFramesAsync()).Count.Should().Be(framesAfterPanic);

        // Only an explicit Start resumes.
        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected);
        hub.Attempts.Should().Be(2);
        statuses.Snapshot().Select(s => s.Connected).Should().Equal(true, false, true);
    }

    [Fact]
    public async Task Panic_is_not_wedged_behind_an_inflight_control_send()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub();
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);

        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected && time.ScheduledTimerCount == 2);

        // Wedge the transport: the next control send parks inside its write while
        // holding the connection's frame write lock.
        hub.Stream(0).ParkWrites();
        time.Advance(TimeSpan.FromMilliseconds(100));
        await hub.Stream(0).WriteParked.WaitAsync(Timeout);

        // Panic must not hang behind the wedged send: the caller's token cancels
        // the wait for the write lock and local teardown still completes. The
        // supervisor's heartbeat gap zeroes the pad regardless.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        Func<Task> act = async () => await adapter.SubmitPanicAsync("panic", cts.Token).WaitAsync(Timeout);
        await act.Should().ThrowAsync<OperationCanceledException>();

        adapter.IsConnected.Should().BeFalse();
        await Task.Delay(50);
        hub.Attempts.Should().Be(1, "panic must never auto-reconnect, even when the panic frame could not be sent");
    }

    [Fact]
    public async Task Disconnect_sends_a_best_effort_zero_then_stays_down_until_restarted()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub();
        var statuses = new StatusLog();
        await using var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);
        adapter.StatusChanged += statuses.Record;

        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected && time.ScheduledTimerCount == 2);

        await adapter.DisconnectAsync(CancellationToken.None).WaitAsync(Timeout);

        var frames = await hub.Stream(0).DecodeCompleteFramesAsync();
        frames[^1].Should().BeOfType<ZeroFrame>();
        adapter.IsConnected.Should().BeFalse();
        await WaitUntilAsync(() => hub.Stream(0).Disposed);
        await WaitUntilAsync(() => statuses.Snapshot().Count == 2);
        statuses.Snapshot().Select(s => s.Connected).Should().Equal(true, false);

        // No reconnect while stopped.
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
        }

        await Task.Delay(50);
        hub.Attempts.Should().Be(1);

        // Idempotent.
        await adapter.DisconnectAsync(CancellationToken.None).WaitAsync(Timeout);
        statuses.Snapshot().Should().HaveCount(2);

        // Explicit restart resumes.
        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected);
        hub.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Disposed_adapter_never_resurrects_even_when_a_drop_races_disposal()
    {
        for (var i = 0; i < 25; i++)
        {
            var time = new ManualTimeProvider();
            var hub = new FakeConnectHub();
            var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);

            adapter.Start();
            await WaitUntilAsync(() => adapter.IsConnected);

            // Race a peer drop (which triggers the reconnect path) against
            // disposal: whatever interleaving results, the adapter must end
            // disconnected, stop connecting, and leak no session.
            var drop = Task.Run(() => hub.Stream(0).CompleteReadWithEof());
            var dispose = Task.Run(async () => await adapter.DisposeAsync());
            await Task.WhenAll(drop, dispose).WaitAsync(Timeout);

            adapter.IsConnected.Should().BeFalse();
            await Task.Delay(20);
            var attempts = hub.Attempts;
            await Task.Delay(30);
            hub.Attempts.Should().Be(attempts, $"a disposed adapter must not keep connecting (iteration {i})");
            adapter.IsConnected.Should().BeFalse();
            await WaitUntilAsync(() => hub.AllStreamsDisposed);
        }
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_start_after_dispose_throws()
    {
        var time = new ManualTimeProvider();
        var hub = new FakeConnectHub();
        var adapter = new SupervisorChannelAdapter(() => hub.CreateClient(time), timeProvider: time);
        adapter.Start();
        await WaitUntilAsync(() => adapter.IsConnected);

        await adapter.DisposeAsync().AsTask().WaitAsync(Timeout);
        Func<Task> again = async () => await adapter.DisposeAsync();
        await again.Should().NotThrowAsync();

        Action restart = () => adapter.Start();
        restart.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Adapter_is_a_pad_state_sink()
    {
        var sessionId = NewSessionId();
        var time = new ManualTimeProvider();
        await using var server = StartServer(sessionId);
        var accept = server.WaitForConnectionAsync();

        await using var adapter = new SupervisorChannelAdapter(sessionId, timeProvider: time);
        IPadStateSink sink = adapter;
        var state = new VirtualPadState { ButtonA = true };
        sink.Push(in state);

        adapter.Start();
        await accept.WaitAsync(Timeout);
        await WaitUntilAsync(() => time.ScheduledTimerCount == 2);

        time.Advance(TimeSpan.FromMilliseconds(100));
        var control = await ReadFrameAsync<ControlFrame>(new FrameCodec(), server);
        control.Payload!.ButtonA.Should().BeTrue();
    }
}
