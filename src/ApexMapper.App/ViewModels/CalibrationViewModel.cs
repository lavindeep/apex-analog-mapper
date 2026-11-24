using System.ComponentModel;
using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Input;

namespace ApexMapper.App.ViewModels;

/// <summary>One analog key on the Calibration page.</summary>
public sealed class CalibrationRowViewModel : ObservableObject
{
    private readonly CalibrationViewModel _owner;
    private (int Reading, int Band, int Index)? _pendingRest;
    private int? _raw;
    private string? _message;
    private string _status = "";
    private string _stateText = "";
    private bool _busy;
    private bool _isExpanded;

    internal CalibrationRowViewModel(ScanCode key, string name, CalibrationViewModel owner)
    {
        _owner = owner;
        Key = key;
        Name = name;
        Learn = new Command(() => owner.Begin(this, CalibrationStep.Learn), () => owner.CanStep(this, CalibrationStep.Learn));
        SetReleased = new Command(() => owner.Begin(this, CalibrationStep.Released), () => owner.CanStep(this, CalibrationStep.Released));
        SetPressed = new Command(() => owner.Begin(this, CalibrationStep.Pressed), () => owner.CanStep(this, CalibrationStep.Pressed));
    }

    public ScanCode Key { get; }

    /// <summary>What a screen reader calls the row.</summary>
    public override string ToString() => Name;

    public string Name { get; }

    /// <summary>The row is open in the list. The page keeps one row open at a time.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (value != _isExpanded && !_owner.CanToggle(this))
            {
                // Tell the expander the row stayed as it was.
                Raise(nameof(IsExpanded));
                return;
            }
            if (Set(ref _isExpanded, value) && value)
            {
                _owner.Opened(this);
            }
        }
    }

    /// <summary>The row's state in a few words, beside its bar: Calibrated, Not calibrated or Calibrate again.</summary>
    public string StateText
    {
        get => _stateText;
        internal set
        {
            if (Set(ref _stateText, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>Saved, and on this sensor and firmware: what the page's summary counts.</summary>
    public bool IsCalibrated { get; internal set; }

    /// <summary>What a screen reader says for the row's header.</summary>
    public string Summary => $"{Name}, {StateText}";

    /// <summary>The app does not know which sensor this key reads, so the steps wait for Find sensor.</summary>
    public bool NeedsSensor => SensorIndex is null;

    /// <summary>What is saved for this key on this board, any firmware.</summary>
    public KeyCalibration? Stored { get; internal set; }

    /// <summary>The sensor the learn step found this session, when it differs from what is saved.</summary>
    internal int? Learned { get; set; }

    /// <summary>A rest reading waiting for its full press, with its noise band.</summary>
    internal (int Reading, int Band, int Index)? PendingRest
    {
        get => _pendingRest;
        set
        {
            _pendingRest = value;
            Raise(nameof(PressedIsNext));
            Raise(nameof(RestMark));
            Raise(nameof(RestMarkText));
            Raise(nameof(BarText));
        }
    }

    /// <summary>Step 2 is the one to do now, so its button is the primary one; otherwise step 1's is.</summary>
    public bool PressedIsNext => _pendingRest is not null;

    /// <summary>The step the last result asks for next, when it names one, for the page to move the keyboard to its button.</summary>
    public CalibrationStep? NextStep { get; internal set; }

    /// <summary>The key to calibrate next, when the last result saved this one, for the page to move the keyboard to its header.</summary>
    public CalibrationRowViewModel? NextKey { get; internal set; }

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
                Raise(nameof(ReadingText));
                Raise(nameof(BarText));
                Raise(nameof(ShowsReading));
                Raise(nameof(ShowsSensorHint));
                Raise(nameof(ShowsWaiting));
                Raise(nameof(WaitingText));
                Raise(nameof(HasReading));
            }
        }
    }

    /// <summary>The live reading as a fraction of the sensor's range, for the dot on the bar.</summary>
    public double Fill => (_raw ?? 0) / (double)KeyCalibration.MaxCount;

    /// <summary>The reading now, for screen readers.</summary>
    public string ReadingText => _raw is { } raw ? $"{raw} of {KeyCalibration.MaxCount}" : "No reading";

    /// <summary>The released reading on the bar: one just taken and waiting for its full press, else the saved one.</summary>
    public double? RestMark => _pendingRest is { } p && p.Index == SensorIndex ? p.Reading / (double)KeyCalibration.MaxCount
        : Stored is { } s && s.SensorIndex == SensorIndex ? s.Rest / (double)KeyCalibration.MaxCount : null;

    public double? FullMark => Stored is { } s && s.SensorIndex == SensorIndex ? s.FullPress / (double)KeyCalibration.MaxCount : null;

    /// <summary>The released mark's number, and whether it is saved yet.</summary>
    public string? RestMarkText => _pendingRest is { } p && p.Index == SensorIndex ? $"Released {p.Reading}, saved after step 2."
        : Stored is { } s && s.SensorIndex == SensorIndex ? $"Saved released reading: {s.Rest}." : null;

    public string? FullMarkText => Stored is { } s && s.SensorIndex == SensorIndex ? $"Saved fully pressed reading: {s.FullPress}." : null;

    /// <summary>The bar's tooltip: the reading now and what each mark is, since the 2 px marks are too thin to hover.</summary>
    public string BarText => $"Reading now: {ReadingText}." + (RestMarkText is { } rest ? " " + rest : "") + (FullMarkText is { } full ? " " + full : "");

    public string Status
    {
        get => _status;
        internal set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(ShowsStatus));
            }
        }
    }

    /// <summary>The saved readings and the reading now show beside the steps, but not while a step runs, when its message is the only text.</summary>
    public bool ShowsStatus => _status.Length > 0 && !_busy;

    public bool ShowsReading => _raw is not null && !_busy;

    /// <summary>The current step's prompt, or how the last one ended.</summary>
    public string? Message
    {
        get => _message;
        internal set => Set(ref _message, value);
    }

    public bool IsBusy
    {
        get => _busy;
        internal set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(ShowsSteps));
                Raise(nameof(ShowsSensorStep));
                Raise(nameof(ShowsSensorHint));
                Raise(nameof(ShowsWaiting));
                Raise(nameof(WaitingText));
                Raise(nameof(ShowsStatus));
                Raise(nameof(ShowsReading));
            }
        }
    }

    /// <summary>The two steps and the Find sensor note show, except while a step runs, when its message is the only instruction.</summary>
    public bool ShowsSteps => !NeedsSensor && !_busy;

    /// <summary>The hint to find the sensor of a key whose dot does not move, once there is a dot to watch.</summary>
    public bool ShowsSensorHint => ShowsSteps && _raw is not null;

    public bool HasReading => _raw is not null;

    /// <summary>The steps show, but no reading has come yet.</summary>
    public bool ShowsWaiting => ShowsSteps && _raw is null;

    /// <summary>What to wait for, except when the readings stopped with a reason, when the page's error says what to do instead.</summary>
    public string? WaitingText => ShowsWaiting && _owner.SensorProblem is null ? "Waiting for the keyboard's readings. Choose a step when its button turns on." : null;

    /// <summary>A key with no known sensor shows only Find sensor, except while it runs.</summary>
    public bool ShowsSensorStep => NeedsSensor && !_busy;

    public Command Learn { get; }

    public Command SetReleased { get; }

    public Command SetPressed { get; }

    internal void RefreshAll()
    {
        Raise(nameof(SensorIndex));
        Raise(nameof(NeedsSensor));
        Raise(nameof(IsCalibrated));
        Raise(nameof(ShowsSteps));
        Raise(nameof(ShowsSensorStep));
        Raise(nameof(ShowsSensorHint));
        Raise(nameof(ShowsWaiting));
        Raise(nameof(WaitingText));
        Raise(nameof(RestMark));
        Raise(nameof(FullMark));
        Raise(nameof(RestMarkText));
        Raise(nameof(FullMarkText));
        Raise(nameof(BarText));
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
/// The Calibration page: one row per analog key of the active profile, with the live
/// raw reading, Set released, Set fully pressed, the learn step and the noise at rest.
/// A key is saved on its own once it has both readings. Set released also records each
/// group's signature, which the session hands to its poller for the desync check. The
/// keyboard is read only while the card is open, a board may be read, and no session
/// runs. On an unverified board the built-in sensor table is not trusted: each key is
/// learned first. Raw Input says which keys are down, so a released reading taken with
/// its key pressed is refused.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject
{
    public const int ReleasedMs = 2000;
    public const int PressedMs = 1000;
    public const int LearnMs = 3000;

    /// <summary>How long the learn step waits before its at-rest reading, so a key that clicked the button (Enter) is up again.</summary>
    public const int SettleMs = 500;

    /// <summary>A step with no fresh reading for this long gives up.</summary>
    public const int NoReadingMs = 3000;

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private IReadOnlyList<CalibrationRowViewModel> _rows = [];
    private bool _isOpen;
    private string? _sensorProblem;
    private string? _loggedProblem;
    private Run? _run;
    private readonly HashSet<ScanCode> _held = [];

    /// <summary>The step in progress: its samples, when the last reading came, and the Raw Input stamp it began at.</summary>
    private sealed class Run(CalibrationRowViewModel row, CalibrationStep step, long started, long from)
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

        public long From { get; } = from;

        /// <summary>The row's key went down during a released step, or was down as a reading was taken.</summary>
        public bool KeyPressed { get; set; }
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
        : _workspace.Board is not { } board ? (_workspace.ReadingFirmware ? "Reading the keyboard's firmware." : "Plug in your Apex Pro and choose it on the Keyboard page.")
        : board.Firmware.Version is null ? "The keyboard's firmware could not be read, so its sensors are not read either. Unplug the keyboard and plug it back in to try again."
        : board.Unsupported ? "The app cannot read this keyboard's sensors. Choose another keyboard on the Keyboard page."
        : !board.CanReadSensors ? "Try this keyboard on the Keyboard page first."
        : _rows.Count == 0 ? "The active profile has no analog keys, so there is nothing to calibrate. Add a binding on the Profile page."
        : null;

    public bool CanCalibrate => Blocker is null;

    /// <summary>The blocker sends the user to the Keyboard page, so the page offers a button to it.</summary>
    public bool BlockerOpensKeyboard => !_workspace.SessionActive && (_workspace.Board is not { } board
        ? !_workspace.ReadingFirmware
        : board.Firmware.Version is not null && (board.Unsupported || !board.CanReadSensors));

    /// <summary>The key list shows when there is something to calibrate, or while mapping holds the keyboard.</summary>
    public bool ShowsKeys => _rows.Count > 0 && (CanCalibrate || _workspace.SessionActive);

    /// <summary>How to use the list, or null while calibration can't run.</summary>
    public string? KeysNote => !CanCalibrate || _run is not null || _sensorProblem is not null ? null
        : _rows.All(r => r.IsCalibrated) ? "Every key is calibrated. Open a key to see its saved readings or calibrate it again."
        : _rows.Any(r => r.NeedsSensor) ? "The app has not been tested with this keyboard, so it finds each key's sensor before it takes the key's readings. Keep the other keys up while a step runs."
        : "Calibrate each key with its two steps. A key that is up sits about a fifth of the way along its bar, and the marks show its saved readings. Keep the other keys up while a step runs.";

    /// <summary>The calibration file's problem, and keys to redo after a firmware change (A15).</summary>
    public string? Problem
    {
        get
        {
            var calibration = _workspace.Calibration;
            var redo = _rows.Where(r => calibration?.FromOtherFirmware.Contains(r.Key) == true).Select(r => r.Name).ToList();
            var firmware = redo.Count == 0 ? null
                : $"{Wording.List(redo)} {(redo.Count == 1 ? "was" : "were")} calibrated on other firmware. The readings are kept; open {(redo.Count == 1 ? "it" : "each")} and press Set released to check they still fit.";
            return string.Join(" ", new[] { calibration?.Problem, firmware }.Where(t => t is not null)) is { Length: > 0 } text ? text : null;
        }
    }

    public string Summary
    {
        get
        {
            var done = _rows.Count(r => r.IsCalibrated);
            return _workspace.Board is not { } board ? (_workspace.ReadingFirmware ? "Reading the keyboard's firmware"
                    : _services.Keyboards.Current.Count == 0 ? "No keyboard plugged in" : "No keyboard chosen")
                : board.Firmware.Version is null ? "Firmware could not be read"
                : board.Unsupported ? "Unavailable for this keyboard"
                : !board.CanReadSensors ? "Waiting for the keyboard check"
                : _rows.Count == 0 ? "No analog keys"
                : $"{done} of {_rows.Count} analog keys in this profile calibrated";
        }
    }

    /// <summary>What the user sees when a step's result can't be written. The exception goes to the log.</summary>
    public const string SaveFailed = "The calibration could not be saved. Try the step again. If it fails again, the log says why.";

    /// <summary>What the user sees when the readings stop with a reason. The reason itself goes to the log.</summary>
    public const string SensorTrouble = "The keyboard stopped sending readings the app can use. Unplug it and plug it back in.";

    /// <summary>That the live readings stopped with a reason, in plain words, or null.</summary>
    public string? SensorProblem
    {
        get => _sensorProblem;
        private set
        {
            if (Set(ref _sensorProblem, value))
            {
                Raise(nameof(KeysNote));
                foreach (var row in _rows)
                {
                    row.RefreshAll();
                }
            }
        }
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
            // The poller's reason is for the log and the maintainer, once per reason; the page says it plainly.
            var problem = _services.Sensor.Problem;
            if (problem is not null && problem != _loggedProblem)
            {
                _services.Log("Calibration sensor problem: " + problem);
            }
            _loggedProblem = problem;
            SensorProblem = problem is null ? null : SensorTrouble;
            // A dot left where the last reading put it would look like a key whose bar does not follow it.
            foreach (var row in _rows)
            {
                row.Raw = null;
            }
            if (_run is { } stalled && nowMs - stalled.LastReading > NoReadingMs)
            {
                Finish(stalled, "No readings came from the keyboard, so this step stopped. Try the step again once the dot moves.");
            }
            return;
        }
        SensorProblem = null;
        _loggedProblem = null;
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

    /// <summary>
    /// A key event from Raw Input, drained on the UI thread. Keeps track of which keys are down
    /// on the chosen keyboard. A key Raw Input places on another keyboard is ignored.
    /// </summary>
    public void OnKey(in RawKeyEvent key)
    {
        if (key.Device == 0 || FromAnotherKeyboard(key.Device))
        {
            return;
        }
        if (!key.Down)
        {
            _held.Remove(key.Code);
            return;
        }
        _held.Add(key.Code);
        // A press stamped before the step, such as Enter clicking the button, counts only if the key is still down at a reading.
        if (_run is { Step: CalibrationStep.Released } run && run.Row.Key == key.Code && key.Ticks >= run.From)
        {
            run.KeyPressed = true;
        }
    }

    /// <summary>Forgets every key held, for when a key-up may have gone missing: see <see cref="MainViewModel.OnActivated"/>.</summary>
    public void ForgetHeldKeys() => _held.Clear();

    private bool FromAnotherKeyboard(nint device) =>
        _services.KeyEvents.ContainerOf(device) is { } container && _workspace.Board is { } board && container != board.Id;

    /// <summary>While a step runs, its row stays open and the others stay closed, so its prompt stays on screen.</summary>
    internal bool CanToggle(CalibrationRowViewModel row) => _run is null;

    /// <summary>The row whose step is running, for the page to keep its prompt in view.</summary>
    public CalibrationRowViewModel? ActiveRow => _run?.Row;

    /// <summary>Opening a row closes the others.</summary>
    internal void Opened(CalibrationRowViewModel row)
    {
        foreach (var other in _rows)
        {
            if (other != row)
            {
                other.IsExpanded = false;
            }
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
        _run = new Run(row, step, _services.NowMs(), _services.Timestamp());
        row.IsBusy = true;
        row.Message = step switch
        {
            CalibrationStep.Learn => "Keep your hands off the keyboard for a moment.",
            CalibrationStep.Released => $"Keep {row.Name} and the keys around it released.",
            _ => $"Hold {row.Name} all the way down until this message changes.",
        };
        RefreshSteps();
    }

    private void Advance(Run run, long nowMs)
    {
        var row = run.Row;
        switch (run.Step)
        {
            case CalibrationStep.Learn when run.Learn is null:
                if (nowMs - run.Started < SettleMs)
                {
                    break;
                }
                run.Learn = new LearnStep(_raw);
                run.Started = nowMs;
                row.Message = $"Now press {row.Name} all the way down and hold it until this message changes.";
                break;
            case CalibrationStep.Learn:
                run.Learn.Observe(_raw);
                if (nowMs - run.Started >= LearnMs)
                {
                    FinishLearn(run, run.Learn.Result());
                }
                break;
            case CalibrationStep.Released:
                // The tick has read Raw Input by now, so a Space that clicked the button on its
                // way up is no longer held, while a key held since before the step still is.
                run.KeyPressed |= _held.Contains(row.Key);
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
            Finish(run, $"Nothing moved. Press Find sensor again and hold {row.Name} all the way down when asked.", CalibrationStep.Learn);
            return;
        }
        if (result.Outcome == LearnOutcome.Ambiguous)
        {
            Finish(run, $"More than one sensor moved. Press Find sensor again and hold only {row.Name}.", CalibrationStep.Learn);
            return;
        }
        if (_rows.FirstOrDefault(r => r != row && r.SensorIndex == result.SensorIndex) is { } other)
        {
            Finish(run, $"That sensor already belongs to {other.Name}. Press Find sensor again and hold only {row.Name}.", CalibrationStep.Learn);
            return;
        }
        if (row.SensorIndex == result.SensorIndex)
        {
            Finish(run, $"{row.Name} already reads the right sensor.");
            return;
        }
        row.Learned = result.SensorIndex;
        row.PendingRest = null;
        UpdateRow(row);
        // The tick read the row before it had a sensor, so give it this tick's reading and the steps turn on at once.
        row.Raw = _raw[result.SensorIndex];
        _services.Log($"Learn step: {row.Name} reads sensor {result.SensorIndex}.");
        Finish(run, $"Found {row.Name}'s sensor. Now do steps 1 and 2.");
    }

    private void FinishReleased(Run run)
    {
        var row = run.Row;
        if (run.KeyPressed)
        {
            Finish(run, $"{row.Name} was pressed while its released reading was taken, so nothing was saved. Let go of it and press Set released again.");
            return;
        }
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
            _services.Log("Calibration could not be saved: " + e.Message);
            Finish(run, SaveFailed);
            return;
        }
        // Saved alone only when it is close to the saved one: a rest that moved further may
        // have been taken with the key part way down, and would spoil a good calibration.
        var full = row.Stored is { } stored && stored.SensorIndex == index && Math.Abs(rest - stored.Rest) <= band ? stored.FullPress : (int?)null;
        if (full is { } known && KeyCalibration.Validate(rest, known, band, index) is null)
        {
            Save(run, new KeyCalibration(rest, known, band, index), $"Released {rest}. Saved, keeping fully pressed {known}. Do step 2 too if {row.Name} no longer reaches full output.");
            return;
        }
        row.PendingRest = (rest, band, index);
        Finish(run, row.Stored is { } saved && saved.SensorIndex == index && full is null
            ? $"Released {rest}, far from the saved {saved.Rest}. If {row.Name} was up, hold it all the way down and press Set fully pressed. If not, let go of it and press Set released again."
            : $"Released {rest}. Now do step 2.",
            CalibrationStep.Pressed);
    }

    private void FinishPressed(Run run)
    {
        var row = run.Row;
        var index = row.SensorIndex!.Value;
        var (rest, band) = RestFor(row, index)!.Value;
        var full = run.Extreme;
        // Keys on a tested board read higher as they go down; a lower reading means the released one was taken pressed.
        if (_workspace.Board is { Verified: true } && full < rest - band)
        {
            row.PendingRest = null;
            Finish(run, $"{row.Name} read lower pressed than released, so its released reading was taken with the key down. Press Set released with the key up, then Set fully pressed again.");
            return;
        }
        if (Math.Abs(full - rest) < band + KeyCalibration.MinimumSpanAboveBand)
        {
            Finish(run, $"{row.Name} did not move far enough from rest. Hold it all the way down, then press Set fully pressed again.", CalibrationStep.Pressed);
            return;
        }
        Save(run, new KeyCalibration(rest, full, band, index), $"Fully pressed {full}. Saved.");
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
            _services.Log("Calibration could not be saved: " + e.Message);
            Finish(run, SaveFailed);
            return;
        }
        run.Row.PendingRest = null;
        run.Row.Learned = null;
        _services.Log($"Calibrated {run.Row.Name}: rest {calibration.Rest}, full {calibration.FullPress}, band {calibration.NoiseBand}, sensor {calibration.SensorIndex}.");
        var next = _rows.FirstOrDefault(r => r != run.Row && !r.IsCalibrated);
        Finish(run, next is null ? done : $"{done} Open {next.Name} to calibrate it next.", nextKey: next);
        LoadCalibration();
    }

    private void Finish(Run run, string message, CalibrationStep? next = null, CalibrationRowViewModel? nextKey = null)
    {
        run.Row.NextKey = nextKey;
        _run = null;
        run.Row.IsBusy = false;
        run.Row.Message = message;
        run.Row.NextStep = next;
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
        if (!CanCalibrate)
        {
            foreach (var row in rows)
            {
                row.IsExpanded = false;
            }
        }
        else if (!rows.Any(r => r.IsExpanded) && rows.FirstOrDefault(r => !r.IsCalibrated) is { } next)
        {
            next.IsExpanded = true;
        }
        Raise(nameof(Summary));
        Raise(nameof(Problem));
        Raise(nameof(Blocker));
        Raise(nameof(BlockerOpensKeyboard));
        Raise(nameof(CanCalibrate));
        Raise(nameof(KeysNote));
        Raise(nameof(ShowsKeys));
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
        var again = row.Stored is not null && (row.Learned is not null || calibration!.FromOtherFirmware.Contains(row.Key));
        row.IsCalibrated = row.Stored is not null && !again;
        row.StateText = row.Stored is null ? (row.NeedsSensor ? "Find sensor first" : "Not calibrated") : again ? "Calibrate again"
            : row.Stored.IsClipping ? "Calibrated, full output early" : "Calibrated";
        row.Status = row.Stored is not { } stored ? ""
            : row.Learned is not null ? $"{row.Name} reads a different sensor now. Do both steps again."
            : calibration!.FromOtherFirmware.Contains(row.Key) ? $"Saved on other firmware: released {stored.Rest}, fully pressed {stored.FullPress}. Press Set released with {row.Name} up to check they still fit."
            : $"Saved readings: released {stored.Rest}, fully pressed {stored.FullPress}." +
              (stored.IsClipping ? $" {row.Name} reaches full output before it bottoms out; the last bit of travel does nothing. That is how its sensor reads, so there is nothing to fix." : "");
        row.RefreshAll();
        Raise(nameof(Summary));
        Raise(nameof(KeysNote));
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.Board):
                CancelRun();
                SensorProblem = null;
                foreach (var row in _rows)
                {
                    row.Learned = null;
                    row.PendingRest = null;
                    row.Message = null;
                    row.IsExpanded = false;
                }
                // An unplugged keyboard sends no key-up for the keys held on it.
                _held.Clear();
                LoadCalibration();
                SyncRows();
                break;
            case nameof(Workspace.Calibration):
            case nameof(Workspace.ActiveProfile):
                SyncRows();
                break;
            case nameof(Workspace.ReadingFirmware):
                Raise(nameof(Blocker));
                Raise(nameof(BlockerOpensKeyboard));
                Raise(nameof(KeysNote));
                Raise(nameof(ShowsKeys));
                Raise(nameof(Summary));
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
            SensorProblem = null;
            foreach (var row in _rows)
            {
                row.Raw = null;
            }
        }
    }

    private void RefreshSteps()
    {
        Raise(nameof(IsBusy));
        Raise(nameof(ActiveRow));
        Raise(nameof(KeysNote));
        foreach (var row in _rows)
        {
            row.RefreshAll();
        }
    }
}
