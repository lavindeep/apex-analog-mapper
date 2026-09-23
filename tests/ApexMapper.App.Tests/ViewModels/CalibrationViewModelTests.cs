using ApexMapper.App.Model;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
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

    /// <summary>Starts a learn step and lets it take its reading at rest.</summary>
    private void BeginLearn(CalibrationViewModel calibration, CalibrationRowViewModel row)
    {
        _h.Sensor.Reading = FakeSensor.AtRest();
        row.Learn.Execute(null);
        Run(calibration, CalibrationViewModel.SettleMs);
    }

    private static RawKeyEvent Key(bool down) => new(DefaultProfiles.Key.W, down, Device: 1, Ticks: 0);

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
    public void The_rest_is_the_mean_the_band_follows_the_noise_and_the_full_press_is_the_deepest_reading()
    {
        var calibration = Open();
        var w = W(calibration);

        w.SetReleased.Execute(null);
        var end = _h.Now + CalibrationViewModel.ReleasedMs;
        for (var i = 0; _h.Now <= end; _h.Now += 50, i++)
        {
            _h.Sensor.Reading = AppHarness.Holding(16, i % 2 == 0 ? (ushort)800 : (ushort)900);
            calibration.Tick(_h.Now);
        }
        // The press starts after the click and eases off before the step ends.
        w.SetPressed.Execute(null);
        foreach (var reading in new ushort[] { 850, 3900, 3700 })
        {
            _h.Sensor.Reading = AppHarness.Holding(16, reading);
            Run(calibration, CalibrationViewModel.PressedMs / 3);
        }
        Run(calibration, CalibrationViewModel.PressedMs / 3);

        Assert.Equal(KeyCalibration.NoiseBandFor(100), w.Stored?.NoiseBand);
        Assert.InRange(w.Stored!.Rest, 849, 851);
        Assert.Equal(3900, w.Stored.FullPress);
    }

    [Fact]
    public void A_released_reading_taken_with_the_key_down_is_refused()
    {
        var calibration = Open();
        var w = W(calibration);
        _h.Sensor.Reading = FakeSensor.AtRest();

        w.SetReleased.Execute(null);
        calibration.OnKey(Key(down: true));
        Run(calibration, CalibrationViewModel.ReleasedMs);

        Assert.Equal("W was pressed while its released reading was taken, so nothing was saved. Let go of it and press Set released again.", w.Message);
        Assert.False(w.SetPressed.CanExecute(null));
        Assert.Empty(_h.Services.Calibrations.Load(AppHarness.Tkl, AppHarness.Firmware).Signatures);

        // Still held when the next step begins.
        w.SetReleased.Execute(null);
        Run(calibration, CalibrationViewModel.ReleasedMs);
        Assert.StartsWith("W was pressed", w.Message);

        calibration.OnKey(Key(down: false));
        SetReleased(calibration);
        Assert.StartsWith("Released 850", w.Message);
    }

    [Fact]
    public void A_new_released_reading_is_saved_alone_only_when_it_is_close_to_the_saved_one()
    {
        _h.Services.Calibrations.Put(AppHarness.Tkl, AppHarness.Firmware, DefaultProfiles.Key.W, KeyCalibration.Create(850, 3900, 40, 16));
        var calibration = Open();
        var w = W(calibration);

        _h.Sensor.Reading = AppHarness.Holding(16, 870);
        w.SetReleased.Execute(null);
        Run(calibration, CalibrationViewModel.ReleasedMs);
        Assert.Equal(new KeyCalibration(870, 3900, 40, 16), w.Stored);
        Assert.Equal("Released 870, noise 0 counts. Saved.", w.Message);

        // Part way down, short of where the key reports a press.
        _h.Sensor.Reading = AppHarness.Holding(16, 2400);
        w.SetReleased.Execute(null);
        Run(calibration, CalibrationViewModel.ReleasedMs);
        Assert.Equal(870, w.Stored!.Rest);
        Assert.StartsWith("Released 2400, noise 0 counts. Now hold W all the way down", w.Message);
    }

    [Fact]
    public void A_full_press_below_rest_on_a_tested_board_means_the_released_reading_was_taken_pressed()
    {
        var calibration = Open();
        var w = W(calibration);
        _h.Sensor.Reading = AppHarness.Holding(16, 3900);
        w.SetReleased.Execute(null);
        Run(calibration, CalibrationViewModel.ReleasedMs);

        _h.Sensor.Reading = FakeSensor.AtRest();
        w.SetPressed.Execute(null);
        Run(calibration, CalibrationViewModel.PressedMs);

        Assert.Null(w.Stored);
        Assert.StartsWith("W read lower pressed than released", w.Message);
        Assert.False(w.SetPressed.CanExecute(null));
    }

    [Fact]
    public void A_key_that_clips_says_so_in_its_status()
    {
        _h.Services.Calibrations.Put(AppHarness.Tkl, AppHarness.Firmware, DefaultProfiles.Key.W, KeyCalibration.Create(850, KeyCalibration.MaxCount, 40, 16));

        var calibration = Open();

        Assert.Equal("Calibrated: released 850 (±40), fully pressed 4095. W reaches full output before it bottoms out; the last bit of travel does nothing.", W(calibration).Status);
        Assert.Equal("Not calibrated. With the key up, press Set released.", calibration.Rows.Single(r => r.Name == "S").Status);
    }

    [Fact]
    public void One_step_runs_at_a_time()
    {
        var calibration = Open();

        W(calibration).SetReleased.Execute(null);

        Assert.All(calibration.Rows, row => Assert.False(row.SetReleased.CanExecute(null)));
        Assert.All(calibration.Rows, row => Assert.False(row.Learn.CanExecute(null)));
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
        Assert.Equal("Not calibrated. Press Learn to find its sensor first.", w.Status);
        Assert.False(w.SetReleased.CanExecute(null));

        BeginLearn(calibration, w);
        _h.Sensor.Reading = AppHarness.Holding(17, 3000);
        Run(calibration, CalibrationViewModel.LearnMs);

        Assert.Equal(17, w.SensorIndex);
        Assert.StartsWith("W reads sensor 17.", w.Message);
        Assert.True(w.SetReleased.CanExecute(null));
    }

    [Fact]
    public void Learn_takes_its_rest_reading_once_the_keys_settle_and_waits_for_a_late_press()
    {
        _h.Workspace.Board = new Board(AppHarness.Gen3Info, new FirmwareReading("1.0", null, null), Consented: true);
        var calibration = Open();
        var w = W(calibration);

        // Enter clicked Learn and is still on its way up.
        w.Learn.Execute(null);
        _h.Sensor.Reading = AppHarness.Holding(50, 3000);
        Run(calibration, CalibrationViewModel.SettleMs / 2);
        _h.Sensor.Reading = FakeSensor.AtRest();
        Run(calibration, CalibrationViewModel.SettleMs + CalibrationViewModel.LearnMs / 2);
        _h.Sensor.Reading = AppHarness.Holding(17, 3000);
        Run(calibration, CalibrationViewModel.LearnMs / 2);

        Assert.False(calibration.IsBusy);
        Assert.Equal(17, w.SensorIndex);
    }

    [Fact]
    public void Learn_refuses_a_sensor_another_key_reads()
    {
        var calibration = Open();
        var w = W(calibration);

        BeginLearn(calibration, w);
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
