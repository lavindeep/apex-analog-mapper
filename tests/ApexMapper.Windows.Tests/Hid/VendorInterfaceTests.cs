using System.Diagnostics;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Hid;

public class VendorInterfaceTests
{
    private readonly byte[] _reply = new byte[SensorProtocol.ReportLength];

    [Fact]
    public void A_good_exchange_returns_the_reply()
    {
        var fake = new FakeVendorStream();
        using var device = new VendorInterface(fake);

        Assert.Equal(ExchangeStatus.Ok, device.Exchange(SensorRequest.Firmware(), _reply));

        Assert.Equal(Fixtures.Firmware, _reply);
        Assert.Equal(1, fake.Writes);
        Assert.Equal(1, fake.Reads);
        Assert.False(fake.SawMalformedRequest);
    }

    [Fact]
    public void A_short_reply_is_a_fault()
    {
        var fake = new FakeVendorStream { OnRead = (_, _, _) => new byte[20] };
        using var device = new VendorInterface(fake);

        Assert.Equal(ExchangeStatus.ShortReply, device.Exchange(SensorRequest.Group(2), _reply));
    }

    [Fact]
    public void A_non_zero_report_id_is_a_fault()
    {
        var bad = Fixtures.RestGroup(2);
        bad[0] = 1;
        var fake = new FakeVendorStream { OnRead = (_, _, _) => bad };
        using var device = new VendorInterface(fake);

        Assert.Equal(ExchangeStatus.BadReportId, device.Exchange(SensorRequest.Group(2), _reply));
    }

    [Fact]
    public void A_timeout_is_a_fault()
    {
        var fake = new FakeVendorStream { OnRead = (_, _, _) => null };
        using var device = new VendorInterface(fake);

        Assert.Equal(ExchangeStatus.Timeout, device.Exchange(SensorRequest.Group(2), _reply));
    }

    /// <summary>
    /// The read runs on a dedicated thread (not the pool, which a stalled runner can
    /// starve) and the test proves it started before aborting. The 200 ms bound is the
    /// CI gate; the mechanism is that Abort disposes the stream, which is what wakes the
    /// read. The hardware run measures the figure.
    /// </summary>
    [Fact(Timeout = 5000)]
    public async Task Abort_unblocks_a_pending_read_at_once()
    {
        var fake = new FakeVendorStream();
        fake.OnRead = (_, _, _) =>
        {
            fake.BlockUntilDisposed();
            return null;
        };
        using var device = new VendorInterface(fake);
        var finished = new TaskCompletionSource<ExchangeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Thread(() => finished.SetResult(device.Exchange(SensorRequest.Group(2), new byte[SensorProtocol.ReportLength]))) { IsBackground = true };
        reader.Start();
        Assert.True(SpinWait.SpinUntil(() => fake.Reads == 1, 2000), "The read never started.");

        var clock = Stopwatch.StartNew();
        device.Abort();
        var status = await finished.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.True(fake.IsDisposed);
        Assert.Equal(ExchangeStatus.Closed, status);
        Assert.True(clock.Elapsed.TotalMilliseconds < 200, $"Took {clock.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.True(device.IsClosed);
    }

    [Fact]
    public void After_abort_every_exchange_is_closed_without_touching_the_stream()
    {
        var fake = new FakeVendorStream();
        using var device = new VendorInterface(fake);
        device.Abort();

        Assert.Equal(ExchangeStatus.Closed, device.Exchange(SensorRequest.Firmware(), _reply));
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public void The_allowlist_is_enforced_before_any_write()
    {
        var fake = new FakeVendorStream();
        using var device = new VendorInterface(fake);

        Assert.Throws<InvalidOperationException>(() => device.Exchange(default, _reply));
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public void The_reply_buffer_must_be_a_full_report()
    {
        using var device = new VendorInterface(new FakeVendorStream());

        Assert.Throws<ArgumentException>(() => device.Exchange(SensorRequest.Firmware(), new byte[64]));
    }
}
