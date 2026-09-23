using ApexMapper.App.Model;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class CalibrationViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private CalibrationViewModel Open()
    {
        _h.Workspace.ActiveProfile ??= DefaultProfiles.Forza();
        if (_h.Workspace.Board is null)
        {
            _h.ChooseTkl();
        }
        return new CalibrationViewModel(_h.Services, _h.Workspace) { IsOpen = true };
    }

    private static CalibrationRowViewModel W(CalibrationViewModel calibration) => calibration.Rows.Single(r => r.Key == DefaultProfiles.Key.W);

    /// <summary>Ticks the card as the window's timer would, for <paramref name="ms"/> of the harness clock.</summary>
    private void Run(CalibrationViewModel calibration, int ms)
    {
        var end = _h.Now + ms;
        for (; _h.Now <= end; _h.Now += 50)
        {
            calibration.Tick(_h.Now);
        }
    }

    private void SetReleased(CalibrationViewModel calibration)
    {
        _h.Sensor.Reading = FakeSensor.AtRest();
        W(calibration).SetReleased.Execute(null);
        Run(calibration, CalibrationViewModel.ReleasedMs);
    }

    [Fact]
    public void The_card_lists_the_profile_s_analog_keys_with_the_built_in_sensors()
    {
        var calibration = Open();

        Assert.Equal(["W", "S", "A", "D"], calibration.Rows.Select(r => r.Name));
        Assert.Equal([16, 30, 29, 31], calibration.Rows.Select(r => r.SensorIndex));
        Assert.Equal("0 of 4 analog keys calibrated", calibration.Summary);
        Assert.True(_h.Sensor.Wanted);
    }

    [Fact]
    public void Set_released_records_every_group_s_signature_and_set_pressed_saves_the_key()
    {
        var calibration = Open();
        var w = W(calibration);

        SetReleased(calibration);

        Assert.Null(w.Stored);
        Assert.False(calibration.IsBusy);
        var signatures = _h.Services.Calibrations.Load(AppHarness.Tkl, AppHarness.Firmware).Signatures;
        Assert.Equal(new GroupSignature(0), signatures[1]);
        Assert.Equal(new GroupSignature(0x2000), signatures[2]);
        Assert.Equal(new GroupSignature(0x1000), signatures[3]);
        Assert.Equal(new GroupSignature(0), signatures[4]);
        Assert.Equal(new GroupSignature(0x3200), signatures[5]);

        _h.Sensor.Reading = AppHarness.Holding(16, 3900);
        w.SetPressed.Execute(null);
        Run(calibration, CalibrationViewModel.PressedMs);

        Assert.Equal(new KeyCalibration(850, 3900, KeyCalibration.NoiseBandFor(0), 16), w.Stored);
        Assert.Equal(w.Stored, _h.Workspace.Calibration!.Keys[DefaultProfiles.Key.W]);
        Assert.Equal("1 of 4 analog keys calibrated", calibration.Summary);
    }

    [Fact]
    public void Set_pressed_is_refused_when_the_key_barely_moved()
    {
        var calibration = Open();
        var w = W(calibration);
        SetReleased(calibration);

        var band = KeyCalibration.NoiseBandFor(0);
        _h.Sensor.Reading = AppHarness.Holding(16, (ushort)(850 + band + KeyCalibration.MinimumSpanAboveBand - 1));
        w.SetPressed.Execute(null);
        Run(calibration, CalibrationViewModel.PressedMs);

        Assert.Null(w.Stored);
        Assert.StartsWith("W did not move far enough from rest.", w.Message);
        Assert.True(w.SetPressed.CanExecute(null));
    }

    [Fact]
    public void An_untested_board_learns_each_key_s_sensor_first()
    {
        _h.Workspace.Board = new Board(AppHarness.Gen3Info, new FirmwareReading("1.0", null, null), Consented: true);
        var calibration = Open();
        var w = W(calibration);
        Assert.Null(w.SensorIndex);
        Assert.False(w.SetReleased.CanExecute(null));

        _h.Sensor.Reading = FakeSensor.AtRest();
        w.Learn.Execute(null);
        calibration.Tick(_h.Now);
        _h.Sensor.Reading = AppHarness.Holding(17, 3000);
        Run(calibration, CalibrationViewModel.LearnMs);

        Assert.Equal(17, w.SensorIndex);
        Assert.StartsWith("W reads sensor 17.", w.Message);
        Assert.True(w.SetReleased.CanExecute(null));
    }

    [Fact]
    public void Learn_refuses_a_sensor_another_key_reads()
    {
        var calibration = Open();
        var w = W(calibration);

        _h.Sensor.Reading = FakeSensor.AtRest();
        w.Learn.Execute(null);
        calibration.Tick(_h.Now);
        _h.Sensor.Reading = AppHarness.Holding(30, 3000);
        Run(calibration, CalibrationViewModel.LearnMs);

        Assert.Equal(16, w.SensorIndex);
        Assert.StartsWith("That sensor already belongs to S.", w.Message);
    }

    [Fact]
    public void A_step_without_readings_gives_up_with_the_sensor_s_problem()
    {
        var calibration = Open();
        var w = W(calibration);
        _h.Sensor.Problem = "The keyboard stopped answering.";
        w.SetReleased.Execute(null);

        calibration.Tick(_h.Now + CalibrationViewModel.NoReadingMs);
        Assert.True(calibration.IsBusy);
        calibration.Tick(_h.Now + CalibrationViewModel.NoReadingMs + 1);

        Assert.False(calibration.IsBusy);
        Assert.EndsWith("The keyboard stopped answering.", w.Message);
        Assert.Equal("The keyboard stopped answering.", calibration.SensorProblem);
    }

    [Fact]
    public void Closing_the_card_or_starting_a_session_stops_reading_the_keyboard()
    {
        var calibration = Open();
        var w = W(calibration);
        w.SetReleased.Execute(null);

        calibration.IsOpen = false;

        Assert.False(calibration.IsBusy);
        Assert.Equal("Stopped.", w.Message);
        Assert.False(_h.Sensor.Wanted);

        calibration.IsOpen = true;
        _h.Workspace.Session = SessionState.Starting;

        Assert.Equal("Stop mapping to calibrate.", calibration.Blocker);
        Assert.False(_h.Sensor.Wanted);
        Assert.False(w.SetReleased.CanExecute(null));
    }

    [Fact]
    public void Keys_calibrated_on_other_firmware_are_kept_and_flagged()
    {
        _h.Services.Calibrations.Put(AppHarness.Tkl, "4.9.1", DefaultProfiles.Key.W, KeyCalibration.Create(850, 3900, 40, 16));

        var calibration = Open();

        Assert.NotNull(W(calibration).Stored);
        Assert.Equal("W was calibrated on other firmware. The readings are kept; calibrate it again to be sure.", calibration.Problem);
        Assert.Equal("0 of 4 analog keys calibrated", calibration.Summary);
    }
}
