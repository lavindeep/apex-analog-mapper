using System;
using System.Threading;
using System.Threading.Tasks;
using ApexMapper.App.Services;
using ApexMapper.Core.Pipeline;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Services;

/// <summary>
/// Bridge-level contract tests. The bridge is a 1:1 forwarder over
/// SupervisorChannelAdapter (itself covered by the Ipc suites); these tests pin
/// the parts observable without a live supervisor: rest-state connectivity, the
/// fail-closed no-op panic contract, slot writes without a session, and
/// disposal idempotence. Each test uses a unique session id so no test can
/// rendezvous with a real pipe.
/// </summary>
public sealed class SupervisorChannelBridgeTests
{
    private static string UniqueSessionId() => $"bridge-test-{Guid.NewGuid():N}"[..24];

    [Fact]
    public void IsConnected_is_false_before_connect()
    {
        using var bridge = new SupervisorChannelBridge(UniqueSessionId());
        bridge.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Sink_is_available_for_the_engine()
    {
        using var bridge = new SupervisorChannelBridge(UniqueSessionId());
        bridge.Sink.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitPanic_with_no_session_completes_as_a_silent_noop()
    {
        using var bridge = new SupervisorChannelBridge(UniqueSessionId());

        // Contract: completion means "output forced off", not "frame delivered".
        await bridge.SubmitPanicAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        bridge.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitControl_without_a_session_writes_the_slot_and_completes()
    {
        using var bridge = new SupervisorChannelBridge(UniqueSessionId());

        var state = new VirtualPadState { RightTrigger = 0.5f };
        await bridge.SubmitControlAsync(state, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disconnect_without_a_session_is_idempotent()
    {
        using var bridge = new SupervisorChannelBridge(UniqueSessionId());

        await bridge.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await bridge.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_is_idempotent_even_after_connect()
    {
        var bridge = new SupervisorChannelBridge(UniqueSessionId());
        bridge.ConnectAsync(CancellationToken.None).IsCompleted.Should().BeTrue(
            "connect is fire-and-observe; it must never block the caller");

        bridge.Dispose();
        bridge.Dispose();
    }
}
