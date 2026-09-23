using ApexMapper.App.Model;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class LiveSensorTests
{
    private readonly Workspace _workspace = new();
    private readonly List<(Guid Board, PollerConfig Config)> _created = [];

    // Its pollers open nothing: they wait for a keyboard that never comes.
    private LiveSensor Create() => new(_workspace, (board, snapshot, config) =>
    {
        _created.Add((board, config));
        return new SensorPoller(() => null, snapshot, config);
    });

    private static Board BoardOf(KeyboardInfo info, bool consented = false) =>
        new(info, new FirmwareReading(AppHarness.Firmware, null, null), consented);

    [Fact]
    public void It_polls_only_while_someone_wants_readings_the_board_may_be_read_and_no_session_runs()
    {
        using var sensor = Create();
        object calibration = new(), preview = new();
        sensor.Want(calibration, true);
        Assert.False(sensor.IsPolling);

        _workspace.Board = BoardOf(AppHarness.Gen3Info);
        Assert.False(sensor.IsPolling);

        _workspace.Board = BoardOf(AppHarness.Gen3Info, consented: true);
        Assert.True(sensor.IsPolling);

        _workspace.Session = SessionState.Starting;
        Assert.False(sensor.IsPolling);

        _workspace.Session = SessionState.Idle;
        sensor.Want(preview, true);
        sensor.Want(calibration, false);
        Assert.True(sensor.IsPolling);

        sensor.Want(preview, false);
        Assert.False(sensor.IsPolling);
        Assert.Equal(2, _created.Count);
    }

    [Fact]
    public void It_reads_every_group_without_signatures()
    {
        using var sensor = Create();
        _workspace.Board = BoardOf(AppHarness.TklInfo);

        sensor.Want(this, true);

        var config = Assert.Single(_created).Config;
        Assert.Equal([1, 2, 3, 4, 5], config.Groups);
        Assert.All(config.Signatures, signature => Assert.Null(signature));
        Assert.False(sensor.TryRead(new ushort[70]));
    }

    [Fact]
    public void Another_board_gets_its_own_poller()
    {
        using var sensor = Create();
        sensor.Want(this, true);

        _workspace.Board = BoardOf(AppHarness.TklInfo);
        _workspace.Board = BoardOf(AppHarness.Gen3Info, consented: true);

        Assert.Equal([AppHarness.Tkl, AppHarness.Gen3], _created.Select(c => c.Board));
        Assert.True(sensor.IsPolling);
    }
}
