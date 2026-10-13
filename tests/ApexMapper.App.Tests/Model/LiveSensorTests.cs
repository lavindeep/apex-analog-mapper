using System.Text;
using ApexMapper.App.Model;
using ApexMapper.App.Tests.ViewModels;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.Model;

public class LiveSensorTests
{
    /// <summary>
    /// Mapping or an unplug stops the poller before the keyboard card ends its check, so
    /// the rejected reply has to outlive the poller for the check to export it.
    /// </summary>
    [Theory]
    [InlineData("mapping starts")]
    [InlineData("unplugged")]
    public void A_rejected_reply_outlives_the_poller(string interruption)
    {
        var workspace = new Workspace { Board = new Board(AppHarness.TklInfo, new FirmwareReading("4.9.1", null, null), Consented: false) };
        using var sensor = new LiveSensor(workspace, (_, snapshot, config) => new SensorPoller(() => new AllZeroGroups(), snapshot, config));
        sensor.Want(this, true);
        Assert.True(SpinWait.SpinUntil(() => sensor.Rejected is not null, 3000));
        var rejected = sensor.Rejected;

        if (interruption == "mapping starts")
        {
            workspace.Session = SessionState.Starting;
        }
        else
        {
            workspace.Board = null;
        }

        Assert.False(sensor.IsPolling);
        Assert.Equal(rejected, sensor.Rejected);
    }

    /// <summary>Answers the firmware request with 4.9.1 and every sensor read with zeros, which the poller rejects.</summary>
    private sealed class AllZeroGroups : IVendorStream
    {
        private byte _command;

        public void Write(byte[] buffer, int offset, int count) => _command = buffer[offset + 1];

        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, SensorProtocol.ReportLength);
            if (_command == SensorRequest.FirmwareCommand)
            {
                Encoding.ASCII.GetBytes("4.9.1").CopyTo(buffer, offset + 1);
            }
            return SensorProtocol.ReportLength;
        }

        public void Dispose()
        {
        }
    }
}
