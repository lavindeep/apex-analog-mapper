using ApexMapper.Output.Ipc;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Supervisor.Tests;

public class SupervisorSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------
    // Harness: a real loopback pipe with the session on the server end and
    // a raw codec writer on the client end. Every await on pipe traffic is
    // bounded — a hang must fail the test, never wedge the run.
    // ------------------------------------------------------------------

    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly FrameCodec _codec = new();
        private long _sequence;

        public required PipePair Pipes { get; init; }
        public required FakeControllerOutput Output { get; init; }
        public required ManualTimeProvider Time { get; init; }
        public required SupervisorSession Session { get; init; }
        public required Task<SessionEndReason> Run { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        public static async Task<SessionHarness> StartAsync(Action<FakeControllerOutput>? configureOutput = null)
        {
            var pipes = await PipePair.CreateAsync();
            var output = new FakeControllerOutput();
            configureOutput?.Invoke(output);
            var time = new ManualTimeProvider();
            var cts = new CancellationTokenSource();
            var session = new SupervisorSession(
                new FrameConnection(pipes.Server), output, new SupervisorOptions(), time);
            return new SessionHarness
            {
                Pipes = pipes,
                Output = output,
                Time = time,
                Cts = cts,
                Session = session,
                Run = session.RunAsync(cts.Token),
            };
        }

        public Task SendControlAsync(PadStatePayload? payload, long? sequence = null) =>
            SendAsync(new ControlFrame
            {
                SchemaVersion = IFrame.CurrentSchemaVersion,
                SequenceNumber = sequence ?? Interlocked.Increment(ref _sequence),
                TimestampTicks = 0,
                Payload = payload,
            });

        public Task SendHeartbeatAsync() =>
            SendAsync(new HeartbeatFrame
            {
                SchemaVersion = IFrame.CurrentSchemaVersion,
                SequenceNumber = Interlocked.Increment(ref _sequence),
            });

        public Task SendZeroAsync() =>
            SendAsync(new ZeroFrame { SchemaVersion = IFrame.CurrentSchemaVersion, Reason = "test" });

        public Task SendPanicAsync() =>
            SendAsync(new PanicFrame { SchemaVersion = IFrame.CurrentSchemaVersion, Reason = "test" });

        public Task SendUnknownVersionControlAsync() =>
            SendAsync(new ControlFrame { SchemaVersion = 99, Payload = new PadStatePayload { ButtonA = true } });

        public Task SendAsync(IFrame frame) =>
            _codec.WriteFrameAsync(Pipes.Client, frame, CancellationToken.None).AsTask().WaitAsync(Timeout);

        /// <summary>Waits until the session has armed its heartbeat timer, so an
        /// Advance cannot race the session's own startup.</summary>
        public Task WaitForWatchdogAsync() => WaitUntilAsync(() => Time.ScheduledTimerCount > 0);

        public Task WaitForSubmitCountAsync(int count) => WaitUntilAsync(() => Output.Submitted.Count >= count);

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Pipes.DisposeAsync();
            try
            {
                await Run.WaitAsync(Timeout);
            }
            catch
            {
                // The session's outcome is asserted by each test; disposal only
                // makes sure the run task is drained before the harness goes away.
            }

            Cts.Dispose();
        }
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

            await Task.Delay(5);
        }
    }

    private static void AssertToreDownExactlyOnce(IReadOnlyList<string> callSnapshot)
    {
        var calls = callSnapshot.ToList();
        calls.Count(c => c == "zero").Should().Be(1);
        calls.Count(c => c == "disconnect").Should().Be(1);
        calls.IndexOf("zero").Should().BeLessThan(calls.IndexOf("disconnect"));
        calls.Skip(calls.IndexOf("zero")).Should().NotContain("submit");
    }

    // ------------------------------------------------------------------
    // Frame dispatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task Control_frame_payload_reaches_the_output()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 0.75f, ButtonA = true });
        await h.WaitForSubmitCountAsync(1);

        h.Output.Submitted[0].LeftTrigger.Should().Be(0.75f);
        h.Output.Submitted[0].ButtonA.Should().BeTrue();
        h.Output.Calls[0].Should().Be("connect");
    }

    [Fact]
    public async Task Null_payload_control_frame_is_counted_and_never_submitted()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendControlAsync(payload: null);
        await h.SendControlAsync(new PadStatePayload { ButtonB = true });
        await h.WaitForSubmitCountAsync(1);

        h.Session.NullPayloadControlFrames.Should().Be(1);
        h.Output.Submitted.Should().HaveCount(1);
        h.Output.Submitted[0].ButtonB.Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_frames_drive_no_output_calls()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendHeartbeatAsync();
        await h.SendHeartbeatAsync();
        // Ordered pipe: once this control frame is submitted, the heartbeats
        // before it have been dispatched.
        await h.SendControlAsync(new PadStatePayload());
        await h.WaitForSubmitCountAsync(1);

        h.Output.Calls.Should().Equal("connect", "submit");
    }

    [Fact]
    public async Task Zero_frame_zeroes_the_pad_and_the_session_continues()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 1f });
        await h.SendZeroAsync();
        await h.SendControlAsync(new PadStatePayload { RightTrigger = 1f });
        await h.WaitForSubmitCountAsync(2);

        h.Output.Calls.Should().Equal("connect", "submit", "zero", "submit");
        h.Run.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Duplicate_and_out_of_order_sequence_numbers_are_applied_as_is()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 0.1f }, sequence: 5);
        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 0.2f }, sequence: 5);
        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 0.3f }, sequence: 3);
        await h.WaitForSubmitCountAsync(3);

        h.Output.Submitted.Select(s => s.LeftTrigger).Should().Equal(0.1f, 0.2f, 0.3f);
        h.Run.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_version_frames_are_counted_and_not_dispatched()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendUnknownVersionControlAsync();
        await h.SendControlAsync(new PadStatePayload { ButtonX = true });
        await h.WaitForSubmitCountAsync(1);

        h.Session.UnknownVersionFrames.Should().Be(1);
        h.Output.Submitted.Should().HaveCount(1);
        h.Output.Submitted[0].ButtonX.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_version_flood_does_not_keep_the_session_alive()
    {
        // Unknown-version frames are dropped by the connection BEFORE the session
        // callback (so they never call NotifyAlive), which means a peer flooding
        // them cannot forge liveness. Continuous unknown-version traffic while the
        // gap window elapses must still tear the pad down.
        await using var h = await SessionHarness.StartAsync();
        await h.WaitForWatchdogAsync();

        // A single valid frame anchors the last-alive time at T0.
        await h.SendControlAsync(new PadStatePayload());
        await h.WaitForSubmitCountAsync(1);

        // Advance partway into the window, then flood unknown-version frames at
        // that later time. Waiting on the drop counter proves they were all
        // processed before the final advance — deterministically, without any
        // valid frame that would legitimately reset liveness.
        h.Time.Advance(TimeSpan.FromMilliseconds(600));
        for (var i = 0; i < 50; i++)
        {
            await h.SendUnknownVersionControlAsync();
        }

        await WaitUntilAsync(() => h.Session.UnknownVersionFrames >= 50);
        h.Run.IsCompleted.Should().BeFalse("600 ms is still inside the 1 s window");

        // Total elapsed since the last VALID frame is now 1200 ms. The gap must
        // fire: if the flooded frames had counted as liveness, the last-alive
        // time would be T0+600 ms and only 600 ms would have elapsed — no gap.
        h.Time.Advance(TimeSpan.FromMilliseconds(600));

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.HeartbeatGap);
        AssertToreDownExactlyOnce(h.Output.Calls);
        h.Session.UnknownVersionFrames.Should().BeGreaterThanOrEqualTo(50, "every flooded frame is counted, none dispatched");
    }

    // ------------------------------------------------------------------
    // Teardown triggers — each must zero then disconnect, exactly once
    // ------------------------------------------------------------------

    [Fact]
    public async Task Panic_frame_tears_down_the_session_zero_then_disconnect()
    {
        await using var h = await SessionHarness.StartAsync();

        await h.SendPanicAsync();

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.Panic);
        h.Output.Calls.Should().Equal("connect", "zero", "disconnect");
    }

    [Fact]
    public async Task Peer_disconnect_tears_down_the_pad()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 1f });
        await h.WaitForSubmitCountAsync(1);

        await h.Pipes.Client.DisposeAsync();

        // Platforms disagree on how a peer close surfaces (clean end-of-stream
        // vs a transport error), so assert the safety outcome, not the reason.
        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().BeOneOf(SessionEndReason.PeerDisconnected, SessionEndReason.Faulted);
        AssertToreDownExactlyOnce(h.Output.Calls);
    }

    [Fact]
    public async Task Gap_beyond_threshold_zeroes_and_disconnects_the_pad()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.WaitForWatchdogAsync();

        h.Time.Advance(TimeSpan.FromMilliseconds(1000));

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.HeartbeatGap);
        h.Output.Calls.Should().Equal("connect", "zero", "disconnect");
    }

    [Fact]
    public async Task Frame_arriving_during_teardown_never_reaches_the_pad()
    {
        using var zeroEntered = new ManualResetEventSlim();
        using var zeroGate = new ManualResetEventSlim();
        await using var h = await SessionHarness.StartAsync(o =>
        {
            o.ZeroEntered = zeroEntered;
            o.ZeroGate = zeroGate;
        });
        await h.WaitForWatchdogAsync();

        // Trip the heartbeat gap on a background thread; the teardown parks
        // inside the gated Zero, holding the teardown window open.
        var advance = Task.Run(() => h.Time.Advance(TimeSpan.FromMilliseconds(1000)));
        zeroEntered.Wait(Timeout).Should().BeTrue();

        // The read loop is still alive mid-teardown: deliver a control frame,
        // give it time to arrive and park, then let the teardown finish.
        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 1f });
        await Task.Delay(250);
        zeroGate.Set();

        (await h.Run.WaitAsync(Timeout)).Should().Be(SessionEndReason.HeartbeatGap);
        await advance.WaitAsync(Timeout);

        // Once teardown has begun the frame must never reach the pad: nothing
        // may follow the zero/disconnect pair.
        var calls = h.Output.Calls.ToList();
        var zeroIndex = calls.IndexOf("zero");
        zeroIndex.Should().BeGreaterThanOrEqualTo(0);
        calls.Skip(zeroIndex).Should().Equal("zero", "disconnect");
    }

    [Fact]
    public async Task Frames_arriving_before_the_threshold_postpone_the_gap()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.WaitForWatchdogAsync();

        await h.SendControlAsync(new PadStatePayload());
        await h.WaitForSubmitCountAsync(1);
        h.Time.Advance(TimeSpan.FromMilliseconds(900));

        await h.SendControlAsync(new PadStatePayload());
        await h.WaitForSubmitCountAsync(2);
        h.Time.Advance(TimeSpan.FromMilliseconds(999));

        h.Run.IsCompleted.Should().BeFalse();
        h.Output.Calls.Should().NotContain("zero");

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.HeartbeatGap);
        AssertToreDownExactlyOnce(h.Output.Calls);
    }

    [Fact]
    public async Task Owner_cancellation_tears_down_with_shutdown_reason()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.SendControlAsync(new PadStatePayload());
        await h.WaitForSubmitCountAsync(1);

        h.Cts.Cancel();

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.Shutdown);
        AssertToreDownExactlyOnce(h.Output.Calls);
    }

    [Fact]
    public async Task Malformed_bytes_fault_the_session_and_tear_down()
    {
        await using var h = await SessionHarness.StartAsync();

        // A length prefix far beyond the frame cap is a protocol violation.
        var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        await h.Pipes.Client.WriteAsync(garbage).AsTask().WaitAsync(Timeout);
        await h.Pipes.Client.FlushAsync().WaitAsync(Timeout);

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.Faulted);
        h.Output.Calls.Should().Equal("connect", "zero", "disconnect");
    }

    [Fact]
    public async Task Submit_failure_faults_the_session_and_still_tears_down()
    {
        await using var h = await SessionHarness.StartAsync(o => o.ThrowOnSubmit = new InvalidOperationException("bus lost"));

        await h.SendControlAsync(new PadStatePayload { ButtonA = true });

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.Faulted);
        h.Output.Calls.Should().Equal("connect", "submit", "zero", "disconnect");
    }

    // ------------------------------------------------------------------
    // Fail-closed edges
    // ------------------------------------------------------------------

    [Fact]
    public async Task Output_connect_failure_fails_the_session_with_no_pad_calls()
    {
        var boom = new InvalidOperationException("no bus");
        await using var h = await SessionHarness.StartAsync(o => o.ThrowOnConnect = boom);

        Func<Task> run = async () => await h.Run.WaitAsync(Timeout);

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("no bus");
        h.Output.Calls.Should().Equal("connect");
    }

    [Fact]
    public async Task Zero_failure_during_teardown_still_disconnects()
    {
        await using var h = await SessionHarness.StartAsync(o => o.ThrowOnZero = new InvalidOperationException("zero failed"));

        await h.SendPanicAsync();

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.Panic);
        h.Output.Calls.Should().Equal("connect", "zero", "disconnect");
    }

    [Fact]
    public async Task Disconnect_failure_during_teardown_is_contained()
    {
        await using var h = await SessionHarness.StartAsync(o => o.ThrowOnDisconnect = new InvalidOperationException("disconnect failed"));

        await h.SendPanicAsync();

        var reason = await h.Run.WaitAsync(Timeout);
        reason.Should().Be(SessionEndReason.Panic);
        h.Output.Calls.Should().Equal("connect", "zero", "disconnect");
    }

    // ------------------------------------------------------------------
    // Teardown races
    // ------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_gap_and_peer_disconnect_tear_down_exactly_once()
    {
        for (var i = 0; i < 15; i++)
        {
            await using var h = await SessionHarness.StartAsync();
            await h.WaitForWatchdogAsync();

            // A client flooding control frames while both teardown triggers race:
            // no submit may ever land after the pad was zeroed.
            var flood = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await h.SendControlAsync(new PadStatePayload { LeftTrigger = 0.5f });
                    }
                }
                catch
                {
                    // The pipe closing under the flood is the expected exit.
                }
            });

            var gap = Task.Run(() => h.Time.Advance(TimeSpan.FromSeconds(1)));
            var drop = Task.Run(() => h.Pipes.Client.Dispose());

            await h.Run.WaitAsync(Timeout);
            await Task.WhenAll(gap, drop).WaitAsync(Timeout);
            await flood.WaitAsync(Timeout);

            AssertToreDownExactlyOnce(h.Output.Calls);
        }
    }
}
