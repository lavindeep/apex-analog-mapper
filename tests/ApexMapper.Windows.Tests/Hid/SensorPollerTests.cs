using System.Diagnostics;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Hid;

public class SensorPollerTests
{
    private static readonly PollerConfig Racing = PollerConfig.For([2, 3], Fixtures.Signatures(2, 3));

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000) => SpinWait.SpinUntil(condition, timeoutMs);

    /// <summary>
    /// Hands out the fake once, then a stream whose first read blocks until Stop aborts
    /// it. The first reopen after a fault is immediate, so without this the re-fault on
    /// the disposed fake would overwrite the reason under test.
    /// </summary>
    private static Func<IVendorStream?> OpenOnce(FakeVendorStream fake)
    {
        var opens = 0;
        return () =>
        {
            if (Interlocked.Increment(ref opens) == 1)
            {
                return fake;
            }
            var blocking = new FakeVendorStream();
            blocking.OnRead = (_, _, _) =>
            {
                blocking.BlockUntilDisposed();
                return null;
            };
            return blocking;
        };
    }

    [Fact]
    public void Runs_verifies_firmware_and_publishes_fresh_snapshots()
    {
        var fake = new FakeVendorStream();
        var shared = new SensorSnapshot();
        using var poller = new SensorPoller(() => fake, shared, Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.Cycles >= 10));

        Assert.Equal(PollerState.Running, poller.State);
        Assert.Equal("4.9.1", poller.Firmware);
        Assert.True(shared.IsFresh(Stopwatch.GetTimestamp()));
        Assert.True(shared.WasRead(16));
        Assert.True(shared.WasRead(29));
        Assert.False(shared.WasRead(0));
        Assert.Equal(0, poller.FaultCount);
        poller.Stop();
        Assert.Equal(PollerState.Stopped, poller.State);
    }

    [Fact]
    public void A_canary_mismatch_retires_the_handle()
    {
        var wrong = Fixtures.Firmware;
        wrong[3] = (byte)'8';
        var fake = new FakeVendorStream
        {
            OnRead = (index, command, selector) => index > 0 && command == SensorRequest.FirmwareCommand ? wrong : FakeVendorStream.DefaultReply(command, selector),
        };
        using var poller = new SensorPoller(OpenOnce(fake), new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.FaultCount == 1));
        poller.Stop();

        Assert.Contains("canary", poller.FaultReason);
        Assert.Equal(SensorPoller.CanaryEveryCycles, poller.Cycles);
        Assert.True(fake.IsDisposed);
    }

    [Fact]
    public void A_shifted_reply_fails_its_signature_and_retires_the_handle()
    {
        var fake = new FakeVendorStream
        {
            OnRead = (index, command, selector) => index >= 5 && selector == 2 ? Fixtures.RestGroup(3) : FakeVendorStream.DefaultReply(command, selector),
        };
        using var poller = new SensorPoller(OpenOnce(fake), new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.FaultCount == 1));
        poller.Stop();

        Assert.Contains("signature", poller.FaultReason);
        Assert.Equal(1, poller.FaultCount);
        Assert.True(fake.IsDisposed);
    }

    [Fact]
    public void A_firmware_reply_in_a_sensor_slot_retires_the_handle()
    {
        var fake = new FakeVendorStream
        {
            OnRead = (index, command, selector) => index == 4 ? Fixtures.Firmware : FakeVendorStream.DefaultReply(command, selector),
        };
        using var poller = new SensorPoller(OpenOnce(fake), new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.FaultCount == 1));
        poller.Stop();

        Assert.Contains("12-bit", poller.FaultReason);
        Assert.Equal(1, poller.FaultCount);
        Assert.True(fake.IsDisposed);
    }

    [Fact]
    public void Held_keys_do_not_fail_the_signature()
    {
        var fake = new FakeVendorStream
        {
            OnRead = (_, command, selector) => command == SensorRequest.GroupCommand ? Fixtures.HeldGroup(selector) : FakeVendorStream.DefaultReply(command, selector),
        };
        using var poller = new SensorPoller(() => fake, new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.Cycles >= 60));

        Assert.Equal(0, poller.FaultCount);
    }

    [Fact]
    public void A_reply_in_flight_across_a_reconfigure_is_discarded()
    {
        var gate = new ManualResetEventSlim(false);
        var fake = new FakeVendorStream();
        fake.OnRead = (index, command, selector) =>
        {
            if (index == 1)
            {
                fake.BlockUntil(gate);
            }
            if (index >= 3)
            {
                fake.BlockUntilDisposed();
                return null;
            }
            return FakeVendorStream.DefaultReply(command, selector);
        };
        var shared = new SensorSnapshot();
        using var poller = new SensorPoller(() => fake, shared, Racing);

        poller.Start();
        Assert.True(WaitUntil(() => fake.Reads == 2));
        poller.Reconfigure(PollerConfig.For([3]));
        gate.Set();
        Assert.True(WaitUntil(() => fake.Reads == 4));
        Thread.Sleep(50);

        Assert.Equal(0, shared.Generation);
        Assert.Equal(1, poller.Cycles);
    }

    [Fact]
    public void One_slow_cycle_leaves_the_snapshot_stale_without_a_fault()
    {
        var fake = new FakeVendorStream();
        fake.OnRead = (index, command, selector) =>
        {
            if (index == 21)
            {
                Thread.Sleep(120);
            }
            return FakeVendorStream.DefaultReply(command, selector);
        };
        var shared = new SensorSnapshot();
        using var poller = new SensorPoller(() => fake, shared, Racing);

        poller.Start();
        Assert.True(WaitUntil(() => fake.Reads == 22));
        Assert.True(WaitUntil(() => !shared.IsFresh(Stopwatch.GetTimestamp()), 200));
        Assert.True(WaitUntil(() => poller.Cycles >= 100));

        Assert.Equal(PollerState.Running, poller.State);
        Assert.Equal(0, poller.FaultCount);
        Assert.True(shared.IsFresh(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Three_slow_cycles_in_a_row_are_a_fault()
    {
        var fake = new FakeVendorStream();
        fake.OnRead = (index, command, selector) =>
        {
            if (index is >= 3 and <= 8)
            {
                Thread.Sleep(55);
            }
            return FakeVendorStream.DefaultReply(command, selector);
        };
        using var poller = new SensorPoller(OpenOnce(fake), new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.FaultCount == 1, 3000));
        poller.Stop();

        Assert.Contains("consecutive", poller.FaultReason);
    }

    [Fact]
    public void A_read_timeout_is_a_fault_and_the_device_is_reopened_at_once()
    {
        var opens = 0;
        var fake = new FakeVendorStream
        {
            OnRead = (index, command, selector) => index == 6 ? null : FakeVendorStream.DefaultReply(command, selector),
        };
        using var poller = new SensorPoller(
            () =>
            {
                opens++;
                return opens == 1 ? fake : new FakeVendorStream();
            },
            new SensorSnapshot(),
            Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.FaultCount == 1));
        Assert.Contains("150 ms", poller.FaultReason);
        var clock = Stopwatch.StartNew();
        Assert.True(WaitUntil(() => poller.State == PollerState.Running && poller.Cycles > 10));
        clock.Stop();

        Assert.Equal(2, opens);
        Assert.Equal(1, poller.FaultCount);
        Assert.True(clock.ElapsedMilliseconds < SensorPoller.BackoffMs / 2, $"Reopen took {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void No_device_waits_and_stop_during_the_backoff_returns_long_before_the_backoff_elapses()
    {
        using var poller = new SensorPoller(() => null, new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => poller.State == PollerState.Waiting));
        var clock = Stopwatch.StartNew();
        poller.Stop();
        clock.Stop();

        Assert.True(clock.Elapsed.TotalMilliseconds < 200, $"Stop took {clock.Elapsed.TotalMilliseconds:F1} ms against a {SensorPoller.BackoffMs} ms backoff.");
        Assert.Equal(PollerState.Stopped, poller.State);
    }

    [Fact]
    public void Stop_during_a_blocked_read_returns_without_waiting_for_the_device()
    {
        var fake = new FakeVendorStream();
        fake.OnRead = (index, command, selector) =>
        {
            if (index >= 3)
            {
                fake.BlockUntilDisposed();
                return null;
            }
            return FakeVendorStream.DefaultReply(command, selector);
        };
        using var poller = new SensorPoller(() => fake, new SensorSnapshot(), Racing);

        poller.Start();
        Assert.True(WaitUntil(() => fake.Reads == 4));
        var clock = Stopwatch.StartNew();
        poller.Stop();
        clock.Stop();

        Assert.True(clock.Elapsed.TotalMilliseconds < 200, $"Stop took {clock.Elapsed.TotalMilliseconds:F1} ms; the scripted read never returns on its own.");
        Assert.Equal(0, poller.FaultCount);
    }

    [Fact]
    public void Cycles_allocate_nothing()
    {
        var fake = new FakeVendorStream();
        var device = new VendorInterface(fake);
        var poller = new SensorPoller(() => fake, new SensorSnapshot(), Racing);
        Assert.Null(poller.VerifyFirmware(device));
        Assert.Null(poller.Cycle(device));

        string? fault = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200 && fault is null; i++)
        {
            fault = poller.Cycle(device);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Null(fault);
        Assert.Equal(0, after - before);
        Assert.Equal(201, poller.Cycles);
    }

    [Fact]
    public void Config_orders_groups_and_rejects_bad_ones()
    {
        var config = PollerConfig.For([3, 2, 3]);

        Assert.Equal([2, 3], config.Groups);
        Assert.All(config.Signatures, s => Assert.Null(s));
        Assert.Throws<ArgumentOutOfRangeException>(() => PollerConfig.For([0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PollerConfig.For([6]));
    }
}
