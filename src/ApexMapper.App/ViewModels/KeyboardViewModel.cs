using System.ComponentModel;
using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.ViewModels;

/// <summary>One board in the keyboard list.</summary>
public sealed record BoardItem(KeyboardInfo Info, string Detail)
{
    public Guid Id => Info.ContainerId;

    public string Name => Info.Name;

    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() => $"{Name}, {Detail}";
}

/// <summary>
/// The Keyboard page: the Apex Pro boards plugged in, the remembered choice, and the
/// firmware the chosen one reports. An unverified model gets the try-it steps: the
/// firmware request is the only thing sent until the user agrees to the risk, then a
/// check that its sensors move when a key is pressed, which names the sensor that
/// moved, and a capture export for a GitHub issue whose form the page fills in. The
/// Calibration page's Find sensor step finds each key's sensor. An unsupported model is told so and never
/// gets the sensor request. The firmware is never asked while a session runs: the
/// session's poller has the board open.
/// </summary>
public sealed class KeyboardViewModel : ObservableObject
{
    public const int CheckTimeoutMs = 10_000;

    public const string ConsentText =
        "To read how far each key is pressed, the app sends this keyboard the request that works on the original " +
        "Apex Pro TKL. No one has checked how this model handles it. It will most likely answer the same way. If the " +
        "keyboard acts oddly afterwards, unplug it and plug it back in. The keyboard's answers stay in the app's " +
        "memory. They go into a file only if you export a capture, and the app never sends them anywhere.";

    /// <summary>Every field a capture file holds, in plain words, shown before anyone presses Export.</summary>
    public static readonly IReadOnlyList<string> CaptureHolds =
    [
        "The app's version, the file's format version, and when you saved the file.",
        "The keyboard's model and product ID, and its firmware version or why it could not be read.",
        "The ID Windows gave this keyboard, and the size of its reports.",
        "The keyboard's answer to the firmware request, if it sent one.",
        "From your last check on this keyboard, if it got that far:",
    ];

    /// <summary>What a capture holds from the last check, listed under the last line of <see cref="CaptureHolds"/>.</summary>
    public static readonly IReadOnlyList<string> CheckHolds =
    [
        "A reading of every sensor with your hands off.",
        "A reading of every sensor while you held a key.",
        "A sensor reply the app couldn't read, if there was one.",
    ];

    public const string CaptureLimits =
        "A capture holds no record of your typing, your profiles or anything else from this PC. Each reading is a " +
        "single moment, so it shows which keys were down right then. The app writes the file only where you choose " +
        "and never uploads it. Anyone can read a file you attach to a GitHub issue.";

    public const string UnsupportedText =
        "The app cannot read how far keys are pressed on this model, so mapping can't use it. Its firmware has no " +
        "request for it that anyone has found, so the app never sends one. If you find a request that works, report " +
        "it with the button below.";

    /// <summary>The guide's steps for adding a model the app does not know, which never shows in the list.</summary>
    public const string AddGuide = "https://github.com/lavindeep/apex-analog-mapper/blob/main/docs/adding-a-keyboard.md#add-a-model-the-app-doesnt-know";

    /// <summary>The guide's steps for reporting a model the app cannot read.</summary>
    public const string ReportGuide = "https://github.com/lavindeep/apex-analog-mapper/blob/main/docs/adding-a-keyboard.md#report-a-model-the-app-cant-read";

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly HashSet<Guid> _consented;
    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private readonly ushort[] _filtered = new ushort[SensorProtocol.SensorCount];
    private IReadOnlyList<BoardItem> _boards = [];
    private BoardItem? _selected;
    private Guid? _remembered;
    private int _probe;
    private bool _reading;
    private CheckPhase _check;
    private long _checkSince;
    private LearnStep? _checkStep;
    private (ushort[] Raw, ushort[] Filtered)? _restCapture;
    private (ushort[] Raw, ushort[] Filtered)? _heldCapture;
    private CapturedReply? _rejectedCapture;
    private CapturedReply? _rejectedBefore;
    private (KeyboardInfo Info, string? Firmware)? _capturedFrom;
    private SensorCheckResult? _checkResult;
    private string? _checkText;
    private bool _exported;
    private string? _exportText;
    private (KeyboardInfo Info, string? Firmware)? _exportedFrom;
    private string _issueUrl = SupportIssue.Form;

    private enum CheckPhase
    {
        None,
        Rest,
        Pressing,
    }

    /// <summary>A check ended with its result on show. Not raised when a result comes back with its board.</summary>
    public event Action? CheckEnded;

    /// <summary>A capture was written to disk.</summary>
    public event Action? CaptureSaved;

    public KeyboardViewModel(AppServices services, Workspace workspace, Guid? remembered, IEnumerable<Guid>? consented)
    {
        _services = services;
        _workspace = workspace;
        _remembered = remembered;
        _consented = [.. consented ?? []];
        Consent = new Command(GiveConsent, () => NeedsConsent);
        Check = new Command(StartCheck, () => CanCheck);
        Export = new Command(ExportCapture, () => _selected is not null && !IsChecking);
        OpenIssue = new Command(() => _services.Open(_issueUrl));
        OpenReportGuide = new Command(() => _services.Open(ReportGuide));
        OpenAddGuide = new Command(() => _services.Open(AddGuide));
        _services.Keyboards.Changed += list => _services.Post(() => OnKeyboards(list));
        _workspace.PropertyChanged += OnWorkspaceChanged;
    }

    public IReadOnlyList<BoardItem> Boards
    {
        get => _boards;
        private set => Set(ref _boards, value);
    }

    /// <summary>The chosen board while it is connected. A null from the view (its list was replaced) is ignored.</summary>
    public BoardItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value.Id == _selected?.Id || _workspace.SessionActive)
            {
                return;
            }
            _remembered = value.Id;
            _workspace.Remember(_services.Settings, s => s with { Keyboard = value.Id });
            _selected = value;
            Raise(nameof(Selected));
            Raise(nameof(Summary));
            Raise(nameof(Description));
            Raise(nameof(Missing));
            Raise(nameof(ChoiceText));
            Sync();
        }
    }

    public bool CanChoose => !_workspace.SessionActive;

    /// <summary>The Keyboard row's line: why the list is off, what to do while no board is chosen, or what the list is for.</summary>
    public string ChoiceText => _workspace.Session == SessionState.Paused ? $"Plug the {_workspace.Board?.Info.Name ?? "same keyboard"} back in to resume mapping"
        : _workspace.SessionActive ? "Stop mapping to choose another keyboard"
        : Missing ?? "The keyboard you use with the app";

    /// <summary>The card header: the chosen board, or why there is none.</summary>
    public string Summary => _selected?.Name ?? (_boards.Count == 0 ? "Not plugged in" : "No keyboard chosen");

    /// <summary>The home page's line: the chosen board and whether it is tested, or why there is none. A passed check counts only for the board and firmware it ran on.</summary>
    public string Description => _selected is not { } board ? Summary
        : $"{board.Name} · {(!IsUnverified ? board.Detail
            : CheckPassed ? "Sensor check passed"
            : NeedsConsent ? "Not tested yet: try it"
            : board.Detail)}";

    /// <summary>What to do while no board is chosen, or null.</summary>
    public string? Missing => _selected is not null ? null
        : _boards.Count == 0 ? "Plug in your Apex Pro. It shows up here as soon as Windows finds it."
        : _remembered is not null ? "The keyboard chosen last time is not plugged in. Choose one of these."
        : "More than one keyboard is plugged in. Choose the one to use.";

    /// <summary>The Firmware row's line, or null while no board is chosen.</summary>
    public string? FirmwareText => _reading ? "Reading..."
        : _workspace.Board is not { } board ? null
        : board.Firmware.Version is { } version ? (board is { Verified: true, FirmwareVerified: false } ? $"{version} · Not tested yet" : version)
        : !board.Info.HasVendorInterface ? "Could not be read, because Windows did not show the app the keyboard's sensor interface. " +
            "Unplug the keyboard and plug it back in to try again."
        : "Could not be read. Unplug the keyboard and plug it back in to try again.";

    /// <summary>
    /// The firmware is not one the app was tested on (H7). It warns and never refuses.
    /// An untested model says so in its own banner, so this stays quiet for one.
    /// </summary>
    public string? FirmwareWarning => _workspace.Board is { Verified: true, Firmware.Version: { } version, FirmwareVerified: false }
        ? $"The app has not been tested with firmware {version}. It will most likely work. If the keyboard's replies look wrong, " +
          "the analog keys work as plain on/off keys, and the status on Home says why."
        : null;

    /// <summary>The board is a model the app has not been verified on but may work; the banner stays for as long as it is chosen (H6).</summary>
    public bool IsUnverified => _workspace.Board is { Verified: false, Unsupported: false };

    /// <summary>The board is a model whose firmware has no sensor request anyone has found.</summary>
    public bool IsUnsupported => _workspace.Board is { Unsupported: true };

    /// <summary>Try-it step 1 is done: the user agreed to have this untested board's sensors read.</summary>
    public bool HasConsented => _workspace.Board is { Verified: false, Unsupported: false, Consented: true };

    /// <summary>Step 2's instructions, naming W as the keyboard layout prints it, as the check's own prompt does.</summary>
    public string CheckHowTo => $"Keep your hands off the keyboard while the check starts. When it asks, hold {_services.KeyName(DefaultProfiles.Key.W)} all the way down.";

    /// <summary>Step 3's line while no finished check collected readings on this board and firmware, or null.</summary>
    public string? ExportNote => _workspace.Board is not { } board || IsChecking
        || (_restCapture is not null && _capturedFrom == (board.Info, board.Firmware.Version)) ? null
        : "No check has collected readings on this keyboard yet, so a capture holds no sensor readings" +
          (_rejectedCapture is not null && _workspace.Board is not null && ResultsHere ? ", only the reply the app couldn't read." : ".") +
          (CheckBlocked is not null || (_checkResult == SensorCheckResult.Failed && ResultsHere) ? ""
            : NeedsConsent ? " Run steps 1 and 2 first if you can." : " Run step 2 first if you can.");

    /// <summary>W's sensor moved, so calibration is the next step.</summary>
    public bool CalibrateNext => CheckPassed && _movedW;

    /// <summary>The sensors may be read and W's sensor has not moved in a check yet, so a check is the next step.</summary>
    public bool CheckNext => HasConsented && !CalibrateNext;
    /// <summary>
    /// The next step after a check that ended with a result, on its own line under it, or
    /// null. Another sensor than W's asks for a retry first.
    /// </summary>
    public string? NextStep => !CheckPassed ? (_checkResult == SensorCheckResult.Failed && _workspace.Board is not null && ResultsHere ? _retry : null)
        : _movedW ? "To drive with this keyboard, calibrate each key. Calibration finds each key's sensor first. " +
            "To help this model join the tested list, share a capture in step 3."
        : $"Run the check again holding only {_services.KeyName(DefaultProfiles.Key.W)} to be sure. If the same sensor moves again, " +
          $"{_services.KeyName(DefaultProfiles.Key.W)} may use a different sensor on your keyboard. Either way, Calibration finds each key's sensor, so you can calibrate to drive.";

    // The last passed check moved the sensor the app maps to W.
    private bool _movedW;

    // What to do after the last failed check.
    private string? _retry;

    /// <summary>Step 1's name for screen readers, with its state.</summary>
    public string Step1Name => HasConsented ? "Step 1 of 3, done: let the app read the sensors" : "Step 1 of 3: let the app read the sensors";

    /// <summary>Step 2's name for screen readers, with its state.</summary>
    public string Step2Name => CalibrateNext ? "Step 2 of 3, done: check the sensors" : "Step 2 of 3: check the sensors";

    /// <summary>Step 1's line once it is done, or null.</summary>
    public string? ConsentNote => HasConsented ? "Allowed. The app can read this keyboard's sensors when it needs them." : null;

    /// <summary>The note for an unsupported model, or null.</summary>
    public string? UnsupportedNote => IsUnsupported ? UnsupportedText : null;

    /// <summary>Try-it step 1 is waiting: the firmware answered and the user has not yet agreed to the sensor request.</summary>
    public bool NeedsConsent => _workspace.Board is { Verified: false, Unsupported: false, Consented: false, Firmware.Version: not null } && !_workspace.SessionActive;

    public bool CanCheck => _workspace.Board is { Verified: false, CanReadSensors: true } && _check == CheckPhase.None && !_workspace.SessionActive;

    /// <summary>Why steps 1 and 2 cannot run for this untested board, or null when they can.</summary>
    public string? CheckBlocked => _workspace.Board is not { Verified: false, Unsupported: false } board ? null
        : _workspace.SessionActive ? "Stop mapping to run steps 1 and 2. Step 3 can still share what the app read."
        : board.Firmware.Version is null ? "The app could not read this keyboard's firmware, so steps 1 and 2 wait until it can. " +
            "Step 3 can still share what the app read."
        : null;

    /// <summary>The sensor check is waiting for readings or a key press.</summary>
    public bool IsChecking => _check != CheckPhase.None;

    /// <summary>Try-it step 2 is done: the last check saw a sensor move.</summary>
    public bool CheckPassed => _checkResult == SensorCheckResult.Passed && _workspace.Board is not null && ResultsHere;

    /// <summary>
    /// The last check ran on the chosen board as it is now, or no board is chosen. A result
    /// hides while another board or firmware is chosen and comes back with its own.
    /// </summary>
    private bool ResultsHere => _workspace.Board is not { } board || _capturedFrom == (board.Info, board.Firmware.Version);

    /// <summary>The last capture was saved from the chosen board as it is now, or no board is chosen.</summary>
    private bool ExportedHere => _workspace.Board is not { } board || _exportedFrom == (board.Info, board.Firmware.Version);

    /// <summary>Step 2's button: once a check has a result here, the next press runs it again.</summary>
    public string CheckLabel => _checkResult is not null && ResultsHere && !IsChecking ? "Check again" : "Check the sensors";

    public string? CheckText
    {
        get => ResultsHere ? _checkText : null;
        private set => Set(ref _checkText, value);
    }

    public Command Consent { get; }

    public Command Check { get; }

    public Command Export { get; }

    /// <summary>A capture was saved this run, so the card offers the page to attach it to.</summary>
    public bool Exported
    {
        get => _exported && ExportedHere;
        private set => Set(ref _exported, value);
    }

    public Command OpenIssue { get; }

    public Command OpenReportGuide { get; }

    public Command OpenAddGuide { get; }

    /// <summary>The picker shows while there is something to pick, or the board a session holds is listed.</summary>
    public bool ShowPicker => Boards.Count > 0 && (CanChoose || _selected is not null);

    /// <summary>
    /// The list is empty and no session holds a board: true also when an Apex Pro the app
    /// doesn't know is plugged in, as it never lists one. A paused session keeps its board.
    /// </summary>
    public bool NoBoards => Boards.Count == 0 && _workspace.Board is null;

    /// <summary>Try-it step 3: where the capture went, or why it could not be saved.</summary>
    public string? ExportText
    {
        get => ExportedHere ? _exportText : null;
        private set => Set(ref _exportText, value);
    }

    /// <summary>Takes a newer keyboard list. Raised off the UI thread by discovery and posted here.</summary>
    public void OnKeyboards(IReadOnlyList<KeyboardInfo> list)
    {
        Boards = [.. list.Where(k => k.Known).Select(k => new BoardItem(k, DetailOf(k)))];
        if (_remembered is null && Boards.Count == 1)
        {
            _remembered = Boards[0].Id;
            _workspace.Remember(_services.Settings, s => s with { Keyboard = _remembered }, byUser: false);
        }
        _selected = Boards.FirstOrDefault(b => b.Id == _remembered);
        Raise(nameof(Selected));
        Raise(nameof(ShowPicker));
        Raise(nameof(Summary));
        Raise(nameof(Description));
        Raise(nameof(Missing));
        Raise(nameof(NoBoards));
        Raise(nameof(ChoiceText));
        Export.Refresh();
        Sync();
    }

    /// <summary>Drives the sensor check. Called by the window's timer.</summary>
    public void Tick(long nowMs)
    {
        if (_check == CheckPhase.None)
        {
            return;
        }
        if (!_services.Sensor.TryRead(_raw, _filtered))
        {
            if (nowMs - _checkSince > CheckTimeoutMs)
            {
                // The poller's reason is for the log and the maintainer; the window says it plainly.
                if (_services.Sensor.Problem is { } problem)
                {
                    _services.Log("Sensor check problem: " + problem);
                }
                EndCheck(_services.Sensor.Problem is not null
                    ? "The keyboard's replies were not sensor readings the app could use."
                    : "No readings came from the keyboard's sensors.",
                    SensorCheckResult.Failed, next: "Run the check again. If it still fails, share a capture in step 3.");
            }
            return;
        }
        if (_check == CheckPhase.Rest)
        {
            // Enter may have clicked the button and still be on its way up.
            if (nowMs - _checkSince < CalibrationViewModel.SettleMs)
            {
                return;
            }
            _restCapture = ([.. _raw], [.. _filtered]);
            _checkStep = new LearnStep(_raw);
            _check = CheckPhase.Pressing;
            _checkSince = nowMs;
            CheckText = $"Hold {_services.KeyName(DefaultProfiles.Key.W)} all the way down.";
            return;
        }
        _checkStep!.Observe(_raw);
        if (_checkStep.AnythingMoved())
        {
            _heldCapture = ([.. _raw], [.. _filtered]);
            var sensor = _checkStep.Result().SensorIndex;
            _movedW = SensorMap.DefaultKeyAt(sensor) == DefaultProfiles.Key.W;
            EndCheck("The sensors respond to key presses.", SensorCheckResult.Passed, WhichSensor(sensor));
        }
        else if (nowMs - _checkSince > CheckTimeoutMs)
        {
            var w = _services.KeyName(DefaultProfiles.Key.W);
            EndCheck("No sensor moved during the check.", SensorCheckResult.Failed,
                next: $"Run it again and hold {w} all the way down when it asks. If nothing moves again, this keyboard may not answer " +
                    "the way the tested ones do. Share a capture in step 3.");
        }
    }

    /// <summary>
    /// For the pass message: the sensor that moved most and the key the built-in map puts
    /// there, because the tests accept a capture only when W moved W's sensor. Any key
    /// still passes, as the check only asks whether the sensors answer.
    /// </summary>
    private string WhichSensor(int sensor)
    {
        var w = _services.KeyName(DefaultProfiles.Key.W);
        var moved = SensorMap.DefaultKeyAt(sensor) switch
        {
            { } key when key == DefaultProfiles.Key.W => null,
            { } key => $"Sensor {sensor} moved, which the app maps to {_services.KeyName(key)}, not {w}.",
            null => $"Sensor {sensor} moved, which has no key in the app's map.",
        };
        return moved ?? $"Sensor {sensor} moved, the one the app maps to {w}.";
    }

    /// <summary>
    /// What a GitHub issue needs to add this board to the tested list (H6). The check's
    /// replies go in only for the board and firmware they came from, so a replug after a
    /// firmware update never labels old replies with the new version.
    /// </summary>
    internal CaptureExport BuildExport(KeyboardInfo info, Board? board)
    {
        var replies = new List<CapturedReply>();
        if (board?.Firmware.Reply is { } firmware)
        {
            replies.Add(new CapturedReply("firmware", 0, Convert.ToHexString(firmware)));
        }
        var fromThisBoard = _capturedFrom == (info, board?.Firmware.Version);
        if (_rejectedCapture is { } rejected && fromThisBoard)
        {
            replies.Add(rejected);
        }
        foreach (var (label, capture) in new[] { ("at rest", _restCapture), ("key held", _heldCapture) })
        {
            if (capture is { } c && fromThisBoard)
            {
                for (var group = 1; group <= SensorRequest.GroupCount; group++)
                {
                    var first = (group - 1) * SensorProtocol.SensorsPerGroup;
                    var reply = SensorProtocol.BuildGroupReply(
                        c.Raw.AsSpan(first, SensorProtocol.SensorsPerGroup),
                        c.Filtered.AsSpan(first, SensorProtocol.SensorsPerGroup));
                    replies.Add(new CapturedReply(label, group, Convert.ToHexString(reply)));
                }
            }
        }
        return new CaptureExport(
            _services.AppVersion,
            DateTimeOffset.Now,
            info.ProductId,
            info.Name,
            board?.Firmware.Version ?? board?.Firmware.Problem ?? "not read",
            info.ContainerId.ToString("D"),
            info.VendorInputLength,
            info.VendorOutputLength,
            replies);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.Board):
                Raise(nameof(NoBoards));
                Raise(nameof(Description));
                Raise(nameof(FirmwareText));
                Raise(nameof(FirmwareWarning));
                Raise(nameof(IsUnverified));
                Raise(nameof(IsUnsupported));
                Raise(nameof(UnsupportedNote));
                Raise(nameof(HasConsented));
                Raise(nameof(Step1Name));
                Raise(nameof(ConsentNote));
                Raise(nameof(CheckPassed));
                Raise(nameof(Step2Name));
                Raise(nameof(NextStep));
                Raise(nameof(CalibrateNext));
                Raise(nameof(CheckNext));
                Raise(nameof(CheckLabel));
                // Results show only for the board and firmware they came from.
                Raise(nameof(CheckText));
                Raise(nameof(Exported));
                Raise(nameof(ExportText));
                RefreshTryIt();
                break;
            case nameof(Workspace.Session):
                Raise(nameof(ChoiceText));
                break;
            case nameof(Workspace.SessionActive):
                Raise(nameof(CanChoose));
                Raise(nameof(ShowPicker));
                Raise(nameof(ChoiceText));
                // The session takes the board, so the check would only time out.
                if (_workspace.SessionActive && IsChecking)
                {
                    EndCheck("The check stopped when mapping started. Run it again.");
                }
                RefreshTryIt();
                if (!_workspace.SessionActive)
                {
                    Sync();
                }
                break;
        }
    }

    /// <summary>
    /// Makes the workspace's board match the choice: asks a newly chosen or replugged
    /// board for its firmware, or clears the board when it is gone. Waits while a
    /// session runs; the session pauses and resumes on the keyboard by itself.
    /// </summary>
    private void Sync()
    {
        if (_workspace.SessionActive)
        {
            return;
        }
        if (_selected is null)
        {
            _probe++;
            SetReading(false);
            _workspace.Board = null;
            if (IsChecking)
            {
                EndCheck("The check stopped because the keyboard was unplugged or changed. Run it again.");
            }
        }
        else if (_workspace.Board?.Info != _selected.Info && !_reading)
        {
            Background.Run(ReadFirmwareAsync(_selected));
        }
    }

    private async Task ReadFirmwareAsync(BoardItem item)
    {
        var probe = ++_probe;
        // Cleared first: the live sensor stops before the request, so the two never share the board.
        // The check ends after that, so a reply the stopping poller rejects goes in its capture.
        _workspace.Board = null;
        if (IsChecking)
        {
            EndCheck("The check stopped because the keyboard was unplugged or changed. Run it again.");
        }
        else
        {
            CancelCheck();
        }
        SetReading(true);
        FirmwareReading reading;
        try
        {
            reading = await _services.ReadFirmware(item.Id);
        }
        catch (Exception e)
        {
            // Nothing thrown by the HID library may leave the card stuck on "reading".
            reading = new FirmwareReading(null, null, e.Message);
        }
        if (probe != _probe)
        {
            return;
        }
        SetReading(false);
        _workspace.Board = new Board(item.Info, reading, _consented.Contains(item.Id));
        _services.Log(reading.Version is { } version
            ? $"Keyboard {item.Name} ({item.Info.ProductId:X4}), firmware {version}."
            : $"Keyboard {item.Name} ({item.Info.ProductId:X4}): {reading.Problem}");
        // The board may have changed while the request was out.
        if (_selected?.Info != item.Info)
        {
            Sync();
        }
    }

    private void SetReading(bool reading)
    {
        if (Set(ref _reading, reading, nameof(FirmwareText)))
        {
            _workspace.ReadingFirmware = reading;
            RefreshTryIt();
        }
    }

    private void GiveConsent()
    {
        if (_workspace.Board is not { } board)
        {
            return;
        }
        _consented.Add(board.Id);
        // Added to what the file holds, which may know boards this run started without.
        _workspace.Remember(_services.Settings, s => s with { ConsentedKeyboards = [.. (s.ConsentedKeyboards ?? []).Append(board.Id).Distinct()] });
        _services.Log($"Consent given to read the sensors of unverified keyboard {board.Info.Name} ({board.Info.ProductId:X4}).");
        _workspace.Board = board with { Consented = true };
        StartCheck();
    }

    /// <summary>
    /// The Keyboard page closed or the window lost focus. A check left running would read
    /// keys typed elsewhere while its prompt is out of sight.
    /// </summary>
    public void StopCheck(string why)
    {
        if (IsChecking)
        {
            EndCheck(why);
        }
    }

    /// <summary>
    /// Starts the check. Another card can keep the live sensor polling between checks, so
    /// a rejected reply it already holds belongs to an earlier check and is set aside.
    /// </summary>
    private void StartCheck()
    {
        _check = CheckPhase.Rest;
        _checkSince = _services.NowMs();
        _restCapture = null;
        _heldCapture = null;
        _rejectedCapture = null;
        _checkResult = null;
        Raise(nameof(Description));
        Raise(nameof(CheckPassed));
        Raise(nameof(Step2Name));
        Raise(nameof(NextStep));
        Raise(nameof(CalibrateNext));
        Raise(nameof(CheckNext));
        Raise(nameof(CheckLabel));
        Exported = false;
        ExportText = null;
        _rejectedBefore = _services.Sensor.Rejected;
        _capturedFrom = _workspace.Board is { } board ? (board.Info, board.Firmware.Version) : null;
        CheckText = "Reading the sensors. Keep your hands off the keyboard for a moment.";
        _services.Sensor.Want(this, true);
        RefreshTryIt();
    }

    /// <param name="result">How the check ended, or null when it stopped before it had an answer.</param>
    /// <param name="detail">Shown after the text but kept out of the log, which names keys only for calibration.</param>
    /// <param name="next">A failed check's next step, shown in bold under the text and kept out of the log.</param>
    private void EndCheck(string text, SensorCheckResult? result = null, string? detail = null, string? next = null)
    {
        CancelCheck();
        _checkResult = result;
        _retry = next;
        Raise(nameof(Description));
        Raise(nameof(CheckPassed));
        Raise(nameof(Step2Name));
        Raise(nameof(NextStep));
        Raise(nameof(CalibrateNext));
        Raise(nameof(CheckNext));
        Raise(nameof(CheckLabel));
        CheckText = detail is null ? text : text + " " + detail;
        _services.Log("Sensor check: " + text);
        CheckEnded?.Invoke();
    }

    /// <summary>
    /// Stops a running check, however it ends, and keeps a reply rejected during it for the
    /// capture. Read after the check lets go of the sensor, since a poller that stops can
    /// still reject a reply on its way out.
    /// </summary>
    private void CancelCheck()
    {
        var running = _check != CheckPhase.None;
        _check = CheckPhase.None;
        _checkStep = null;
        _services.Sensor.Want(this, false);
        if (running && _services.Sensor.Rejected is { } rejected && !ReferenceEquals(rejected, _rejectedBefore))
        {
            _rejectedCapture = rejected;
        }
        RefreshTryIt();
    }

    private void ExportCapture()
    {
        // Taken before the dialog: an unplug handled inside its modal loop clears the choice and the board.
        var info = _selected!.Info;
        var board = _workspace.Board is { } b && b.Id == info.ContainerId ? b : null;
        var path = _services.Dialogs.AskSavePath($"apex-capture-{info.ProductId:x4}.json");
        if (path is null)
        {
            return;
        }
        _exportedFrom = (info, board?.Firmware.Version);
        try
        {
            File.WriteAllText(path, BuildExport(info, board).ToJson());
            _issueUrl = IssueUrl(info, board);
            _exported = true;
            var folder = Path.GetDirectoryName(path) is { } dir && Path.GetFileName(dir) is { Length: > 0 } name ? name : Path.GetDirectoryName(path);
            _exportText = $"Saved {Path.GetFileName(path)} in {folder}. Press Open a new issue. The form fills in what the app knows. " +
                "Fill in the rest and attach the file.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _exported = false;
            _exportText = "The capture could not be saved: " + e.Message + " Export again and choose another folder.";
        }
        // Both may hold the same values for another board, so the page is told either way.
        Raise(nameof(Exported));
        Raise(nameof(ExportText));
        if (_exported)
        {
            CaptureSaved?.Invoke();
        }
    }

    /// <summary>
    /// The keyboard support form filled in for the board just exported. The check's result
    /// counts only for the board and firmware it ran on, as in <see cref="BuildExport"/>.
    /// </summary>
    internal string IssueUrl(KeyboardInfo info, Board? board)
    {
        var check = _capturedFrom == (info, board?.Firmware.Version) ? _checkResult : SensorCheckResult.NotRun;
        return SupportIssue.Url(info.Name, info.ProductId, board?.Firmware.Version, KnownKeyboards.Find(info.ProductId)?.Support, check, _services.AppVersion);
    }

    private void RefreshTryIt()
    {
        Raise(nameof(CheckBlocked));
        Raise(nameof(CheckNext));
        Raise(nameof(ExportNote));
        Raise(nameof(NeedsConsent));
        Raise(nameof(CanCheck));
        Raise(nameof(IsChecking));
        Consent.Refresh();
        Check.Refresh();
        Export.Refresh();
    }

    private static string DetailOf(KeyboardInfo info) => (KnownKeyboards.Find(info.ProductId)?.Support, info.HasVendorInterface) switch
    {
        (SensorSupport.Unsupported, _) => "Not supported",
        (_, false) => "Sensors not found",
        (SensorSupport.Verified, _) => "Tested",
        _ => "Not tested yet",
    };
}
