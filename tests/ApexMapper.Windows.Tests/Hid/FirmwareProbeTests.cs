using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Hid;

public class FirmwareProbeTests
{
    [Fact]
    public void Reads_the_version_with_one_firmware_request_and_closes_the_interface()
    {
        var commands = new List<byte>();
        var fake = new FakeVendorStream { OnRead = (_, command, selector) => { commands.Add(command); return FakeVendorStream.DefaultReply(command, selector); } };

        var reading = FirmwareProbe.Read(() => fake);

        Assert.Equal("4.9.1", reading.Version);
        Assert.Null(reading.Problem);
        Assert.Equal(Fixtures.Firmware, reading.Reply);
        Assert.Equal([SensorRequest.FirmwareCommand], commands);
        Assert.True(fake.IsDisposed);
    }

    /// <summary>Try-it step 1: a reply that is not a version stops the flow, and the reply is kept for the capture export.</summary>
    [Fact]
    public void A_reply_that_is_not_a_version_is_kept_with_the_reason()
    {
        var sensorReply = Fixtures.RestGroup(1);
        var fake = new FakeVendorStream { OnRead = (_, _, _) => sensorReply };

        var reading = FirmwareProbe.Read(() => fake);

        Assert.Null(reading.Version);
        Assert.NotNull(reading.Problem);
        Assert.Equal(sensorReply, reading.Reply);
    }

    [Fact]
    public void No_interface_or_no_answer_gives_the_reason_and_no_reply()
    {
        var absent = FirmwareProbe.Read(() => null);
        var silent = FirmwareProbe.Read(() => new FakeVendorStream { OnRead = (_, _, _) => null });

        Assert.Equal(SensorPoller.WaitingReason, absent.Problem);
        Assert.Contains("150 ms", silent.Problem);
        Assert.Null(silent.Reply);
    }
}
