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
