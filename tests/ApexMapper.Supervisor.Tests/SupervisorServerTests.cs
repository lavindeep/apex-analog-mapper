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

        /// <summary>The next session's pad will fail to connect with this error.</summary>
        public void FailNextPadConnect(Exception error)
        {
            lock (_lock)
            {
                _connectFailures.Enqueue(error);
            }
        }

        public static ServerHarness Start()
        {
            var harness = new ServerHarness();
            harness.Server = new SupervisorServer(
                harness.SessionId, harness.CreateOutput, new SupervisorOptions(), harness.Time);
            harness.Server.SessionEnded += reason =>
            {
                lock (harness._lock)
                {
                    harness._endedReasons.Add(reason);
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

        public Task WaitForEndedCountAsync(int count) => WaitUntilAsync(() => EndedReasons.Count >= count);

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
}
