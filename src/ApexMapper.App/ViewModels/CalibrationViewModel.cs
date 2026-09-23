using System.ComponentModel;
using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;

namespace ApexMapper.App.ViewModels;

/// <summary>One analog key on the calibration card.</summary>
public sealed class CalibrationRowViewModel : ObservableObject
{
    private int? _raw;
    private string? _message;
    private string _status = "";
    private bool _busy;

    internal CalibrationRowViewModel(ScanCode key, string name, CalibrationViewModel owner)
    {
        Key = key;
        Name = name;
        Learn = new Command(() => owner.Begin(this, CalibrationStep.Learn), () => owner.CanStep(this, CalibrationStep.Learn));
        SetReleased = new Command(() => owner.Begin(this, CalibrationStep.Released), () => owner.CanStep(this, CalibrationStep.Released));
        SetPressed = new Command(() => owner.Begin(this, CalibrationStep.Pressed), () => owner.CanStep(this, CalibrationStep.Pressed));
    }

    public ScanCode Key { get; }

    public string Name { get; }

    /// <summary>What is saved for this key on this board, any firmware.</summary>
    public KeyCalibration? Stored { get; internal set; }

    /// <summary>The sensor the learn step found this session, when it differs from what is saved.</summary>
    internal int? Learned { get; set; }

    /// <summary>A rest reading waiting for its full press, with its noise band.</summary>
    internal (int Reading, int Band, int Index)? PendingRest { get; set; }

    /// <summary>The sensor this key reads, or null until the learn step finds it.</summary>
    public int? SensorIndex { get; internal set; }

    /// <summary>The live raw reading, 0 to 4095, or null when there is none.</summary>
    public int? Raw
    {
        get => _raw;
        internal set
        {
            if (Set(ref _raw, value))
            {
                Raise(nameof(Fill));
            }
        }
    }

    /// <summary>The live reading as a fraction of the sensor's range, for the bar.</summary>
    public double Fill => (_raw ?? 0) / (double)KeyCalibration.MaxCount;

    public double? RestMark => Stored is { } s && s.SensorIndex == SensorIndex ? s.Rest / (double)KeyCalibration.MaxCount : null;

    public double? FullMark => Stored is { } s && s.SensorIndex == SensorIndex ? s.FullPress / (double)KeyCalibration.MaxCount : null;

    public string Status
    {
        get => _status;
        internal set => Set(ref _status, value);
    }

    /// <summary>The current step's prompt, or how the last one ended.</summary>
    public string? Message
    {
        get => _message;
        internal set => Set(ref _message, value);
    }

    public bool IsBusy
    {
        get => _busy;
        internal set => Set(ref _busy, value);
    }

    public Command Learn { get; }

    public Command SetReleased { get; }

    public Command SetPressed { get; }

    internal void RefreshAll()
    {
        Raise(nameof(SensorIndex));
        Raise(nameof(RestMark));
        Raise(nameof(FullMark));
        Learn.Refresh();
        SetReleased.Refresh();
        SetPressed.Refresh();
    }
}

public enum CalibrationStep
{
    /// <summary>Find which sensor the key reads (H5).</summary>
    Learn,

    /// <summary>Rest reading and noise, and every group's signature.</summary>
    Released,

    Pressed,
}

/// <summary>
/// The calibration card: one row per analog key of the active profile, with the live
/// raw reading, Set released, Set fully pressed, the learn step and the noise at rest.
/// A key is saved on its own once it has both readings. Set released also records each
/// group's signature, which the session hands to its poller for the desync check. The
/// keyboard is read only while the card is open, a board may be read, and no session
/// runs. On an unverified board the built-in sensor table is not trusted: each key is
/// learned first.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject
{
    public const int ReleasedMs = 2000;
    public const int PressedMs = 1000;
    public const int LearnMs = 3000;

    /// <summary>A step with no fresh reading for this long gives up.</summary>
    public const int NoReadingMs = 3000;

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private IReadOnlyList<CalibrationRowViewModel> _rows = [];
    private bool _isOpen;
    private string? _sensorProblem;
    private Run? _run;

    /// <summary>The step in progress: its samples, and when the last reading came.</summary>
    private sealed class Run(CalibrationRowViewModel row, CalibrationStep step, long started)
    {
        public CalibrationRowViewModel Row { get; } = row;
        public CalibrationStep Step { get; } = step;
        public long Started { get; set; } = started;
        public long LastReading { get; set; } = started;
        public long Sum { get; set; }
        public int Count { get; set; }
        public int Min { get; set; } = int.MaxValue;
        public int Max { get; set; } = int.MinValue;
        public int Extreme { get; set; } = -1;
        public LearnStep? Learn { get; set; }
    }

    public CalibrationViewModel(AppServices services, Workspace workspace)
    {
        _services = services;
        _workspace = workspace;
        _workspace.PropertyChanged += OnWorkspaceChanged;
        LoadCalibration();
        SyncRows();
    }

    public IReadOnlyList<CalibrationRowViewModel> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    /// <summary>The card is expanded; the keyboard is read only then.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value))
            {
                if (!value)
                {
                    CancelRun();
                }
                WantSensor();
            }
        }
    }

    /// <summary>Why calibration cannot run now, or null.</summary>
    public string? Blocker => _workspace.SessionActive ? "Stop mapping to calibrate."
        : _workspace.Board is not { } board ? "Choose a keyboard on the keyboard card first."
        : board.Firmware.Version is null ? "The keyboard's firmware could not be read, so its sensors are not read either."
        : !board.CanReadSensors ? "Try this keyboard on the keyboard card first."
        : _rows.Count == 0 ? "The active profile has no analog keys."
        : null;

    public bool CanCalibrate => Blocker is null;

    /// <summary>The calibration file's problem, and keys to redo after a firmware change (A15).</summary>
    public string? Problem
    {
        get
        {
            var calibration = _workspace.Calibration;
            var redo = _rows.Where(r => calibration?.FromOtherFirmware.Contains(r.Key) == true).Select(r => r.Name).ToList();
            var firmware = redo.Count == 0 ? null
                : $"{Names(redo)} {(redo.Count == 1 ? "was" : "were")} calibrated on other firmware. The readings are kept; calibrate {(redo.Count == 1 ? "it" : "them")} again to be sure.";
            return string.Join(" ", new[] { calibration?.Problem, firmware }.Where(t => t is not null)) is { Length: > 0 } text ? text : null;
        }
    }

    public string Summary
    {
        get
        {
            var done = _rows.Count(r => r.Stored is not null && _workspace.Calibration?.FromOtherFirmware.Contains(r.Key) != true);
            return _rows.Count == 0 ? "No analog keys" : $"{done} of {_rows.Count} analog keys calibrated";
        }
    }

    /// <summary>Why the live readings stopped, or null.</summary>
    public string? SensorProblem
    {
        get => _sensorProblem;
        private set => Set(ref _sensorProblem, value);
    }

    public bool IsBusy => _run is not null;

    /// <summary>Moves the bars and drives the step in progress. Called by the window's timer.</summary>
    public void Tick(long nowMs)
    {
        if (!_isOpen || !CanCalibrate)
        {
            return;
        }
        if (!_services.Sensor.TryRead(_raw))
        {
            SensorProblem = _services.Sensor.Problem;
            if (_run is { } stalled && nowMs - stalled.LastReading > NoReadingMs)
            {
                Finish(stalled, "No readings came from the keyboard, so this step stopped. " + _services.Sensor.Problem);
            }
            return;
        }
        SensorProblem = null;
        foreach (var row in _rows)
        {
            row.Raw = row.SensorIndex is { } index ? _raw[index] : null;
        }
        if (_run is { } run)
        {
            run.LastReading = nowMs;
            Advance(run, nowMs);
        }
    }

    internal bool CanStep(CalibrationRowViewModel row, CalibrationStep step) => CanCalibrate && _run is null && step switch
    {
        CalibrationStep.Learn => true,
        CalibrationStep.Released => row.SensorIndex is not null,
        _ => row.SensorIndex is { } index && RestFor(row, index) is not null,
    };

    internal void Begin(CalibrationRowViewModel row, CalibrationStep step)
    {
        if (!CanStep(row, step))
        {
            return;
        }
        _run = new Run(row, step, _services.NowMs());
        row.IsBusy = true;
        row.Message = step switch
        {
            CalibrationStep.Learn => "Keep your hands off the keyboard for a moment.",
            CalibrationStep.Released => $"Keep {row.Name} and the keys around it released.",
            _ => $"Hold {row.Name} all the way down.",
        };
        RefreshSteps();
    }

    private void Advance(Run run, long nowMs)
    {
        var row = run.Row;
        switch (run.Step)
        {
            case CalibrationStep.Learn when run.Learn is null:
                run.Learn = new LearnStep(_raw);
                run.Started = nowMs;
                row.Message = $"Now press {row.Name} all the way down and hold it.";
                break;
            case CalibrationStep.Learn:
                run.Learn.Observe(_raw);
                if (nowMs - run.Started >= LearnMs)
                {
                    FinishLearn(run, run.Learn.Result());
                }
                break;
            case CalibrationStep.Released:
                var rest = _raw[row.SensorIndex!.Value];
                run.Sum += rest;
                run.Count++;
                run.Min = Math.Min(run.Min, rest);
                run.Max = Math.Max(run.Max, rest);
                if (nowMs - run.Started >= ReleasedMs)
                {
                    FinishReleased(run);
                }
                break;
            case CalibrationStep.Pressed:
                var index = row.SensorIndex!.Value;
                var reading = _raw[index];
                var from = RestFor(row, index)!.Value.Reading;
                if (run.Extreme < 0 || Math.Abs(reading - from) > Math.Abs(run.Extreme - from))
                {
                    run.Extreme = reading;
                }
                if (nowMs - run.Started >= PressedMs)
                {
                    FinishPressed(run);
                }
                break;
        }
    }

    private void FinishLearn(Run run, LearnResult result)
    {
        var row = run.Row;
        if (result.Outcome == LearnOutcome.NothingMoved)
        {
            Finish(run, $"Nothing moved. Press Learn again and hold {row.Name} all the way down.");
            return;
        }
        if (result.Outcome == LearnOutcome.Ambiguous)
        {
            Finish(run, $"More than one sensor moved. Press Learn again and hold only {row.Name}.");
            return;
        }
        if (_rows.FirstOrDefault(r => r != row && r.SensorIndex == result.SensorIndex) is { } other)
        {
            Finish(run, $"That sensor already belongs to {other.Name}. Press Learn again and hold only {row.Name}.");
            return;
        }
        if (row.SensorIndex == result.SensorIndex)
        {
            Finish(run, $"{row.Name} reads sensor {result.SensorIndex}, as expected.");
            return;
        }
        row.Learned = result.SensorIndex;
        row.PendingRest = null;
        UpdateRow(row);
        _services.Log($"Learn step: {row.Name} reads sensor {result.SensorIndex}.");
        Finish(run, $"{row.Name} reads sensor {result.SensorIndex}. Now press Set released, then Set fully pressed.");
    }

    private void FinishReleased(Run run)
    {
        var row = run.Row;
        var index = row.SensorIndex!.Value;
        var rest = (int)Math.Round(run.Sum / (double)run.Count);
        var noise = run.Max - run.Min;
        var band = KeyCalibration.NoiseBandFor(noise);
        var board = _workspace.Board!;
        try
        {
            // Every group, taken now: slots with no sensor read under 50 whatever keys are held.
            for (var group = 1; group <= SensorRequest.GroupCount; group++)
            {
                var slots = _raw.AsSpan((group - 1) * SensorProtocol.SensorsPerGroup, SensorProtocol.SensorsPerGroup);
                _services.Calibrations.PutSignature(board.Id, board.Firmware.Version!, group, GroupSignature.FromRest(slots));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Finish(run, "The calibration could not be saved: " + e.Message);
            return;
        }
        var full = row.Stored is { } stored && stored.SensorIndex == index ? stored.FullPress : (int?)null;
        if (full is { } known && KeyCalibration.Validate(rest, known, band, index) is null)
        {
            Save(run, new KeyCalibration(rest, known, band, index), $"Rest {rest}, noise {noise} counts. Saved.");
            return;
        }
        row.PendingRest = (rest, band, index);
        Finish(run, $"Rest {rest}, noise {noise} counts. Now hold {row.Name} all the way down and press Set fully pressed.");
    }

    private void FinishPressed(Run run)
    {
        var row = run.Row;
        var index = row.SensorIndex!.Value;
        var (rest, band) = RestFor(row, index)!.Value;
        var full = run.Extreme;
        if (Math.Abs(full - rest) < band + KeyCalibration.MinimumSpanAboveBand)
        {
            Finish(run, $"{row.Name} did not move far enough from rest. Hold it all the way down, then press Set fully pressed again.");
            return;
        }
        var calibration = new KeyCalibration(rest, full, band, index);
        var clipping = calibration.IsClipping
            ? $" {row.Name} reaches the sensor's limit before the bottom of its travel, so the last part of the press does nothing."
            : "";
        Save(run, calibration, $"Full press {full}. Saved.{clipping}");
    }

    private void Save(Run run, KeyCalibration calibration, string done)
    {
        var board = _workspace.Board!;
        try
        {
            _services.Calibrations.Put(board.Id, board.Firmware.Version!, run.Row.Key, calibration);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Finish(run, "The calibration could not be saved: " + e.Message);
            return;
        }
        run.Row.PendingRest = null;
        run.Row.Learned = null;
        _services.Log($"Calibrated {run.Row.Name}: rest {calibration.Rest}, full {calibration.FullPress}, band {calibration.NoiseBand}, sensor {calibration.SensorIndex}.");
        Finish(run, done);
        LoadCalibration();
    }

    private void Finish(Run run, string message)
    {
        _run = null;
        run.Row.IsBusy = false;
        run.Row.Message = message;
        RefreshSteps();
    }

    /// <summary>The rest reading a full press is measured from: the one just taken, or the saved one for this sensor.</summary>
    private static (int Reading, int Band)? RestFor(CalibrationRowViewModel row, int index) =>
        row.PendingRest is { } pending && pending.Index == index ? (pending.Reading, pending.Band)
        : row.Stored is { } stored && stored.SensorIndex == index ? (stored.Rest, stored.NoiseBand)
        : null;

    private void LoadCalibration()
    {
        var board = _workspace.Board;
        if (board?.Firmware.Version is not { } firmware)
        {
            _workspace.Calibration = null;
            return;
        }
        _workspace.Calibration = _services.Calibrations.Load(board.Id, firmware);
        if (_workspace.Calibration.Problem is { } problem)
        {
            _services.Log(problem);
        }
    }

    /// <summary>One row per analog key of the active profile, keeping rows that stay so their messages survive a reload.</summary>
    private void SyncRows()
    {
        var keys = _workspace.ActiveProfile?.AnalogKeys(_workspace.Map).ToList() ?? [];
        var existing = _rows.ToDictionary(r => r.Key);
        var rows = keys.Select(k => existing.GetValueOrDefault(k) ?? new CalibrationRowViewModel(k, _services.KeyName(k), this)).ToList();
        if (_run is { } run && !rows.Contains(run.Row))
        {
            _run = null;
        }
        foreach (var row in rows)
        {
            UpdateRow(row);
        }
        Rows = rows;
        Raise(nameof(Summary));
        Raise(nameof(Problem));
        Raise(nameof(Blocker));
        Raise(nameof(CanCalibrate));
        RefreshSteps();
        WantSensor();
    }

    private void UpdateRow(CalibrationRowViewModel row)
    {
        var calibration = _workspace.Calibration;
        row.Stored = calibration?.Keys.GetValueOrDefault(row.Key);
        var board = _workspace.Board;
        row.SensorIndex = row.Learned ?? row.Stored?.SensorIndex
            ?? (board is { Verified: true } && SensorMap.Default.TryGetSensorIndex(row.Key, out var index) ? index : null);
        row.Status = row.Stored is not { } stored ? (row.SensorIndex is null ? "Press Learn to find its sensor." : "Not calibrated.")
            : row.Learned is not null ? "Learned a new sensor. Set released and fully pressed again."
            : calibration!.FromOtherFirmware.Contains(row.Key) ? $"Calibrated on other firmware: rest {stored.Rest}, full press {stored.FullPress}."
            : $"Rest {stored.Rest}, full press {stored.FullPress}, noise band {stored.NoiseBand}.{(stored.IsClipping ? " Reaches the sensor's limit before the bottom." : "")}";
        row.RefreshAll();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.Board):
                foreach (var row in _rows)
                {
                    row.Learned = null;
                    row.PendingRest = null;
                    row.Message = null;
                }
                CancelRun();
                LoadCalibration();
                SyncRows();
                break;
            case nameof(Workspace.Calibration):
            case nameof(Workspace.ActiveProfile):
                SyncRows();
                break;
            case nameof(Workspace.SessionActive):
                CancelRun();
                SyncRows();
                break;
        }
    }

    private void CancelRun()
    {
        if (_run is { } run)
        {
            Finish(run, "Stopped.");
        }
    }

    private void WantSensor()
    {
        _services.Sensor.Want(this, _isOpen && CanCalibrate);
        if (!_isOpen || !CanCalibrate)
        {
            foreach (var row in _rows)
            {
                row.Raw = null;
            }
        }
    }

    private void RefreshSteps()
    {
        Raise(nameof(IsBusy));
        foreach (var row in _rows)
        {
            row.RefreshAll();
        }
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
