using ApexMapper.Output;
using ApexMapper.Output.Ipc;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Supervisor.Tests;

public class SupervisorServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------
    // Harness: a running server on a short per-test pipe name, a fresh fake
    // pad per session, and the real tray-side client. Every await on pipe
    // traffic is bounded — a hang must fail the test, never wedge the run.
    // ------------------------------------------------------------------

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly List<FakeControllerOutput> _outputs = new();
        private readonly List<SessionEndReason> _endedReasons = new();
        private readonly List<string> _diagnostics = new();
        private readonly Queue<Exception> _connectFailures = new();
        private readonly object _lock = new();

        public string SessionId { get; } = "s" + Guid.NewGuid().ToString("N")[..8];
        public ManualTimeProvider Time { get; } = new();
        public SupervisorServer Server { get; private set; } = null!;

        public IReadOnlyList<FakeControllerOutput> Outputs
        {
            get
            {
                lock (_lock)
                {
                    return _outputs.ToArray();
                }
            }
        }

        public IReadOnlyList<SessionEndReason> EndedReasons
        {
            get
            {
                lock (_lock)
                {
                    return _endedReasons.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Diagnostics
        {
            get
            {
                lock (_lock)
                {
                    return _diagnostics.ToArray();
                }
            }
        }

        /// <summary>The next session's pad will fail to connect with this error.</summary>
        public void FailNextPadConnect(Exception error)
        {
            lock (_lock)
            {
                _connectFailures.Enqueue(error);
            }
        }

        public static ServerHarness Start(SupervisorOptions? options = null)
        {
            var harness = new ServerHarness();
            harness.Server = new SupervisorServer(
                harness.SessionId, harness.CreateOutput, options ?? new SupervisorOptions(), harness.Time);
            harness.Server.SessionEnded += reason =>
            {
                lock (harness._lock)
                {
                    harness._endedReasons.Add(reason);
                }
            };
            harness.Server.Diagnostics += line =>
            {
                lock (harness._lock)
                {
                    harness._diagnostics.Add(line);
                }
            };
            harness.Server.Start();
            return harness;
        }

        public async Task<SupervisorClient> ConnectClientAsync()
        {
            var client = new SupervisorClient(SessionId, Timeout);
            await client.ConnectAsync(CancellationToken.None).WaitAsync(Timeout);
            return client;
        }

        public Task WaitForWatchdogAsync() => WaitUntilAsync(() => Time.ScheduledTimerCount > 0);

        /// <summary>Waits until the loop (on its own thread) has armed the idle
        /// deadline — the only timer while no session is live — so a subsequent
        /// <see cref="ManualTimeProvider.Advance"/> can reach it.</summary>
        public Task WaitForIdleTimerAsync() => WaitUntilAsync(() => Time.ScheduledTimerCount == 1);

        public Task WaitForEndedCountAsync(int count) => WaitUntilAsync(() => EndedReasons.Count >= count);

        public Task WaitForDiagnosticAsync(Func<string, bool> predicate) =>
            WaitUntilAsync(() => Diagnostics.Any(predicate));

        public async ValueTask DisposeAsync() => await Server.DisposeAsync().AsTask().WaitAsync(Timeout);

        private IControllerOutput CreateOutput()
        {
            var output = new FakeControllerOutput();
            lock (_lock)
            {
                if (_connectFailures.TryDequeue(out var failure))
                {
                    output.ThrowOnConnect = failure;
                }

                _outputs.Add(output);
            }

            return output;
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

    private static void AssertToreDownExactlyOnce(FakeControllerOutput output)
    {
        var calls = output.Calls.ToList();
        calls.Count(c => c == "zero").Should().Be(1);
        calls.Count(c => c == "disconnect").Should().Be(1);
        calls.IndexOf("zero").Should().BeLessThan(calls.IndexOf("disconnect"));
        calls.Skip(calls.IndexOf("zero")).Should().NotContain("submit");
    }

    [Fact]
    public async Task Client_connects_and_drives_the_pad()
    {
        await using var harness = ServerHarness.Start();

        await using var client = await harness.ConnectClientAsync();
        await client.SubmitControlAsync(
            new PadStatePayload { LeftTrigger = 0.6f, ButtonA = true }, CancellationToken.None).WaitAsync(Timeout);
        await client.SubmitHeartbeatAsync(CancellationToken.None).WaitAsync(Timeout);

        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        harness.Outputs[0].Submitted[0].LeftTrigger.Should().Be(0.6f);
        harness.Outputs[0].Submitted[0].ButtonA.Should().BeTrue();
        harness.Outputs[0].Calls[0].Should().Be("connect");
    }

    [Fact]
    public async Task Client_dispose_tears_down_the_session_and_a_second_client_gets_a_fresh_one()
    {
        await using var harness = ServerHarness.Start();

        var client1 = await harness.ConnectClientAsync();
        await client1.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);

        await client1.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);
        AssertToreDownExactlyOnce(harness.Outputs[0]);

        await using var client2 = await harness.ConnectClientAsync();
        await client2.SubmitControlAsync(new PadStatePayload { RightTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);

        await WaitUntilAsync(() => harness.Outputs.Count == 2 && harness.Outputs[1].Submitted.Count == 1);
        harness.Outputs[1].Submitted[0].RightTrigger.Should().Be(1f);
        // The first pad stays torn down; the second session drives a fresh one.
        AssertToreDownExactlyOnce(harness.Outputs[0]);
    }

    [Fact]
    public async Task Heartbeat_gap_frees_a_wedged_session_for_the_next_client()
    {
        await using var harness = ServerHarness.Start();

        // Client 1 connects, proves liveness once, then goes silent WITHOUT
        // closing its pipe — the wedged-tray case the watchdog exists for.
        await using var client1 = await harness.ConnectClientAsync();
        await client1.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        await harness.WaitForWatchdogAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(1));

        await harness.WaitForEndedCountAsync(1);
        harness.EndedReasons[0].Should().Be(SessionEndReason.HeartbeatGap);
        AssertToreDownExactlyOnce(harness.Outputs[0]);

        await using var client2 = await harness.ConnectClientAsync();
        await client2.SubmitControlAsync(new PadStatePayload { ButtonB = true }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 2 && harness.Outputs[1].Submitted.Count == 1);
        harness.Outputs[1].Submitted[0].ButtonB.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_during_an_active_session_zeroes_and_disconnects_the_pad()
    {
        await using var harness = ServerHarness.Start();

        await using var client = await harness.ConnectClientAsync();
        await client.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);

        await harness.Server.StopAsync().WaitAsync(Timeout);

        AssertToreDownExactlyOnce(harness.Outputs[0]);
        harness.EndedReasons.Should().Equal(SessionEndReason.Shutdown);
    }

    [Fact]
    public async Task Stop_while_idle_accepting_completes_cleanly()
    {
        var harness = ServerHarness.Start();

        Func<Task> stop = () => harness.Server.StopAsync().WaitAsync(Timeout);

        await stop.Should().NotThrowAsync();
        harness.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pad_connect_failure_is_contained_and_the_next_session_recovers()
    {
        await using var harness = ServerHarness.Start();
        harness.FailNextPadConnect(new InvalidOperationException("no bus"));

        // The first client reaches the pipe, but its session dies at pad connect.
        var client1 = await harness.ConnectClientAsync();
        await WaitUntilAsync(() => harness.Server.FailedSessionStarts == 1);
        harness.Outputs[0].Calls.Should().Equal("connect");
        await client1.DisposeAsync().AsTask().WaitAsync(Timeout);

        // The accept loop survived: a second client drives a fresh, working pad.
        await using var client2 = await harness.ConnectClientAsync();
        await client2.SubmitControlAsync(new PadStatePayload { ButtonY = true }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 2 && harness.Outputs[1].Submitted.Count == 1);
        harness.Outputs[1].Submitted[0].ButtonY.Should().BeTrue();
        harness.EndedReasons.Should().NotContain(SessionEndReason.HeartbeatGap);
    }

    [Fact]
    public async Task Start_twice_throws()
    {
        await using var harness = ServerHarness.Start();

        var act = () => harness.Server.Start();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Stop_is_idempotent()
    {
        var harness = ServerHarness.Start();

        await harness.Server.StopAsync().WaitAsync(Timeout);
        Func<Task> again = () => harness.Server.StopAsync().WaitAsync(Timeout);

        await again.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var harness = ServerHarness.Start();

        await harness.Server.DisposeAsync().AsTask().WaitAsync(Timeout);
        Func<Task> again = () => harness.Server.DisposeAsync().AsTask().WaitAsync(Timeout);

        await again.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_throwing_session_ended_subscriber_does_not_kill_the_accept_loop()
    {
        await using var harness = ServerHarness.Start();
        harness.Server.SessionEnded += _ => throw new InvalidOperationException("bad subscriber");

        var client1 = await harness.ConnectClientAsync();
        await client1.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        await client1.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);

        // The subscriber threw after the first session ended; the loop must
        // still accept and serve a fresh client.
        await using var client2 = await harness.ConnectClientAsync();
        await client2.SubmitControlAsync(new PadStatePayload { ButtonX = true }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 2 && harness.Outputs[1].Submitted.Count == 1);
        harness.Outputs[1].Submitted[0].ButtonX.Should().BeTrue();
    }

    [Fact]
    public async Task A_throwing_diagnostics_subscriber_does_not_kill_the_accept_loop()
    {
        await using var harness = ServerHarness.Start();
        harness.Server.Diagnostics += _ => throw new InvalidOperationException("bad subscriber");

        // Diagnostics fire on the accept loop itself (session start/end lines),
        // so a throwing subscriber attacks the loop directly on every session.
        var client1 = await harness.ConnectClientAsync();
        await client1.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        await client1.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);

        await using var client2 = await harness.ConnectClientAsync();
        await client2.SubmitControlAsync(new PadStatePayload { ButtonY = true }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 2 && harness.Outputs[1].Submitted.Count == 1);
        harness.Outputs[1].Submitted[0].ButtonY.Should().BeTrue();
    }

    [Fact]
    public async Task Diagnostics_report_session_start_and_end_with_frame_counters()
    {
        await using var harness = ServerHarness.Start();

        var client = await harness.ConnectClientAsync();
        await client.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        await harness.WaitForDiagnosticAsync(line => line.Contains("session started", StringComparison.Ordinal));

        await client.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);

        // The end line carries the reason and both per-session anomaly counters
        // so an operator can see a clean disconnect versus a noisy one.
        await harness.WaitForDiagnosticAsync(line =>
            line.Contains("session ended", StringComparison.Ordinal) &&
            line.Contains(nameof(SessionEndReason.PeerDisconnected), StringComparison.Ordinal) &&
            line.Contains("unknownVersionFrames=0", StringComparison.Ordinal) &&
            line.Contains("nullPayloadControlFrames=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pre_connect_pad_failure_is_reported_with_its_error_message()
    {
        await using var harness = ServerHarness.Start();
        harness.FailNextPadConnect(new InvalidOperationException("no ViGEmBus"));

        var client = await harness.ConnectClientAsync();
        await WaitUntilAsync(() => harness.Server.FailedSessionStarts == 1);

        // The pre-connect failure detail must reach diagnostics instead of being
        // discarded, so a driverless machine reports why every session dies.
        await harness.WaitForDiagnosticAsync(line =>
            line.Contains("failed to start", StringComparison.Ordinal) &&
            line.Contains("no ViGEmBus", StringComparison.Ordinal));
        harness.Server.PipeFailures.Should().Be(0);

        await client.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Pipe_creation_failure_increments_PipeFailures_and_is_reported()
    {
        // Connect a client so the harness server definitively owns the single
        // pipe instance for its session (no race over who binds first); a second
        // server bound to the same name then cannot create the pipe and must
        // keep the failure visible instead of spinning silently.
        await using var harness = ServerHarness.Start();
        await using var client = await harness.ConnectClientAsync();
        await client.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);

        var failures = new List<string>();
        var second = new SupervisorServer(
            harness.SessionId, () => new FakeControllerOutput(), new SupervisorOptions(), harness.Time);
        second.Diagnostics += line =>
        {
            lock (failures)
            {
                failures.Add(line);
            }
        };
        await using (second.ConfigureAwait(false))
        {
            second.Start();

            await WaitUntilAsync(() => second.PipeFailures >= 1);
            second.PipeFailures.Should().BeGreaterThanOrEqualTo(1);
            lock (failures)
            {
                failures.Should().Contain(line => line.Contains("pipe creation failed", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task A_permanently_failing_pipe_still_retires_the_server_at_the_idle_deadline()
    {
        // First harness's client owns the single pipe instance for the session,
        // so the second server's pipe creation fails on every attempt. The idle
        // deadline must still retire the second server — otherwise a server
        // that can never bind would retry in backoff forever, which is exactly
        // the lingering-process behavior idle exit exists to remove.
        await using var harness = ServerHarness.Start();
        await using var client = await harness.ConnectClientAsync();

        var time = new ManualTimeProvider();
        var options = new SupervisorOptions();
        var second = new SupervisorServer(
            harness.SessionId, () => new FakeControllerOutput(), options, time);
        await using (second.ConfigureAwait(false))
        {
            second.Start();
            await WaitUntilAsync(() => second.PipeFailures >= 1);
            await WaitUntilAsync(() => time.ScheduledTimerCount == 1);

            time.Advance(options.IdleExitTimeout + TimeSpan.FromMilliseconds(1));

            var reason = await second.Completion.WaitAsync(Timeout);
            reason.Should().Be(
                ServerExitReason.IdleTimeout,
                "a server that cannot bind its pipe must exit at the idle deadline instead of retrying forever");
        }
    }

    [Fact]
    public async Task Idle_exit_when_no_client_ever_connects()
    {
        await using var harness = ServerHarness.Start();
        var window = new SupervisorOptions().IdleExitTimeout;
        await harness.WaitForIdleTimerAsync();

        harness.Time.Advance(window - TimeSpan.FromSeconds(1));
        harness.Server.Completion.IsCompleted.Should().BeFalse(
            "the idle window has not elapsed yet");

        harness.Time.Advance(TimeSpan.FromSeconds(2));

        var reason = await harness.Server.Completion.WaitAsync(Timeout);
        reason.Should().Be(ServerExitReason.IdleTimeout);
        harness.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_resets_the_idle_window_and_a_fresh_full_window_runs_after_the_session_ends()
    {
        await using var harness = ServerHarness.Start();
        var window = new SupervisorOptions().IdleExitTimeout;
        await harness.WaitForIdleTimerAsync();

        // Halfway into the start window a client connects and is served.
        harness.Time.Advance(window / 2);
        var client = await harness.ConnectClientAsync();
        await client.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        harness.Server.Completion.IsCompleted.Should().BeFalse();

        await client.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);
        await harness.WaitForIdleTimerAsync();

        // The original start deadline has long passed; only a fresh full window
        // measured from the session's end may retire the server.
        harness.Time.Advance(window - TimeSpan.FromSeconds(1));
        harness.Server.Completion.IsCompleted.Should().BeFalse(
            "the fresh post-session idle window has not elapsed yet");

        harness.Time.Advance(TimeSpan.FromSeconds(2));

        var reason = await harness.Server.Completion.WaitAsync(Timeout);
        reason.Should().Be(ServerExitReason.IdleTimeout);
    }

    [Fact]
    public async Task A_session_outliving_the_idle_window_is_never_torn_down_and_a_fresh_window_runs_after_it()
    {
        // The heartbeat gap is stretched so advancing far past the idle window
        // cannot end the session as a side effect: only the idle deadline is in
        // question here, and it must not run while a session is live.
        var options = new SupervisorOptions { HeartbeatGapBeforeZero = TimeSpan.FromMinutes(30) };
        await using var harness = ServerHarness.Start(options);
        var window = options.IdleExitTimeout;

        var client = await harness.ConnectClientAsync();
        await client.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);

        harness.Time.Advance(window * 10);
        harness.Server.Completion.IsCompleted.Should().BeFalse(
            "an active session must never trip the idle exit, however long it lives");
        harness.EndedReasons.Should().BeEmpty();

        await client.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);
        harness.EndedReasons[0].Should().Be(SessionEndReason.PeerDisconnected);
        await harness.WaitForIdleTimerAsync();

        harness.Time.Advance(window - TimeSpan.FromSeconds(1));
        harness.Server.Completion.IsCompleted.Should().BeFalse(
            "a session end must start a fresh full window, not inherit an expired one");

        harness.Time.Advance(TimeSpan.FromSeconds(2));

        var reason = await harness.Server.Completion.WaitAsync(Timeout);
        reason.Should().Be(ServerExitReason.IdleTimeout);
    }

    [Fact]
    public async Task A_client_reconnecting_within_the_post_session_window_is_served_normally()
    {
        await using var harness = ServerHarness.Start();
        var window = new SupervisorOptions().IdleExitTimeout;

        var client1 = await harness.ConnectClientAsync();
        await client1.SubmitControlAsync(new PadStatePayload { LeftTrigger = 1f }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 1 && harness.Outputs[0].Submitted.Count == 1);
        await client1.DisposeAsync().AsTask().WaitAsync(Timeout);
        await harness.WaitForEndedCountAsync(1);
        await harness.WaitForIdleTimerAsync();

        // Halfway into the post-session window a second client reconnects and
        // gets a full working session on a fresh pad.
        harness.Time.Advance(window / 2);
        await using var client2 = await harness.ConnectClientAsync();
        await client2.SubmitControlAsync(new PadStatePayload { ButtonB = true }, CancellationToken.None).WaitAsync(Timeout);
        await WaitUntilAsync(() => harness.Outputs.Count == 2 && harness.Outputs[1].Submitted.Count == 1);
        harness.Outputs[1].Submitted[0].ButtonB.Should().BeTrue();
        harness.Server.Completion.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_mid_idle_window_completes_promptly_and_is_distinguishable_from_idle_exit()
    {
        var harness = ServerHarness.Start();
        await harness.WaitForIdleTimerAsync();
        harness.Time.Advance(new SupervisorOptions().IdleExitTimeout / 2);

        await harness.Server.StopAsync().WaitAsync(Timeout);

        var reason = await harness.Server.Completion.WaitAsync(Timeout);
        reason.Should().Be(ServerExitReason.Stopped);
    }

    [Fact]
    public async Task A_connection_racing_the_idle_deadline_is_served_and_never_torn_down()
    {
        // Each iteration races a real client connect against the idle deadline.
        // Whichever wins, the outcome must be coherent: an accepted connection
        // is served (the loop keeps running), a lost race is a clean idle exit.
        for (var i = 0; i < 5; i++)
        {
            await using var harness = ServerHarness.Start();
            await harness.WaitForIdleTimerAsync();

            var client = new SupervisorClient(harness.SessionId, TimeSpan.FromSeconds(1));
            var connect = client.ConnectAsync(CancellationToken.None);
            harness.Time.Advance(new SupervisorOptions().IdleExitTimeout + TimeSpan.FromSeconds(1));

            await WaitUntilAsync(() => harness.Server.Completion.IsCompleted || harness.Outputs.Count > 0);

            if (harness.Outputs.Count > 0)
            {
                // The server accepted this client: it must be served end to end,
                // never torn down because the deadline fired concurrently.
                await connect.WaitAsync(Timeout);
                await client.SubmitControlAsync(new PadStatePayload { ButtonA = true }, CancellationToken.None).WaitAsync(Timeout);
                await WaitUntilAsync(() => harness.Outputs[0].Submitted.Count == 1);
                harness.Server.Completion.IsCompleted.Should().BeFalse();
            }
            else
            {
                var reason = await harness.Server.Completion.WaitAsync(Timeout);
                reason.Should().Be(ServerExitReason.IdleTimeout);
                harness.Outputs.Should().BeEmpty();
                try
                {
                    // The client lost the race; its connect attempt may fail or
                    // time out, but must never produce a half-served session.
                    await connect.WaitAsync(Timeout);
                }
                catch (Exception)
                {
                    // Expected on this arm.
                }
            }

            await client.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }
}
