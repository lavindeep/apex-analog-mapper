using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.ViewModels;

/// <summary>How the status card looks: its icon and colour.</summary>
public enum StatusTone
{
    Ready,
    NotReady,
    Busy,
    Mapping,
    Paused,
}

/// <summary>
/// The status card: the session's state and why it last stopped, Start and Stop, why
/// Start is unavailable with a way to fix it when there is one (calibration, the
/// driver), the sensor cycle and controller update rate while mapping, and the
/// warnings. Session events arrive on the session thread and are posted here; the card
/// also reads the session's status every <see cref="RefreshMs"/>. It logs state
/// changes, why a session ended, each warning as it appears and the sensor fault behind
/// a fallback, never keys (E10).
/// </summary>
public sealed class StatusViewModel : ObservableObject
{
    public const int RefreshMs = 250;

    /// <summary>How long a key must read the sensor's limit, past its calibrated full press, before the card warns (A16).</summary>
    public const int AtLimitWarningMs = 3000;

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private SessionStatus _status;
    /// <summary>Why the last Start failed, until the next one; null after one that worked.</summary>
    private string? _startFailure;
    private Elevation _gameElevation = Elevation.Visible;
    private bool _gameSeen;
    private string? _blocker;
    private string _blockerTitle = "Not ready";
    private bool _blockedByCalibration;
    private bool _blockedByDriver;
    private bool _blockedByKeyboard;
    /// <summary>The warnings and sensor faults logged since the last session ended: each is logged once a session.</summary>
    private readonly HashSet<string> _logged = [];
    private string? _timing;
    private HashSet<ScanCode> _mappedKeys = [];
    private bool _otherKeyboardSeen;
    private bool _otherKeyboardShown;
    private readonly GameViewModel _game;
    private (long Count, long AtMs)? _rateFrom;
    private double _rate;
    private SessionState _loggedState;
    private readonly Dictionary<ScanCode, long> _atLimitSince = [];
    private List<ScanCode> _stuckAtLimit = [];

    /// <summary>Keys that pressed past their calibration this run, with the calibration they had then. Kept after Stop until that calibration changes.</summary>
    private readonly Dictionary<ScanCode, KeyCalibration?> _recalibrate = [];

    public StatusViewModel(AppServices services, Workspace workspace, GameViewModel game)
    {
        _services = services;
        _workspace = workspace;
        _game = game;
        _status = services.Session.Status();
        Start = new Command(() => Background.Run(StartAsync()), () => CanStart);
        Stop = new Command(() => Background.Run(StopAsync()), () => _workspace.SessionActive);
        GoToCalibration = new Command(() => CalibrationRequested?.Invoke());
        GoToKeyboard = new Command(() => KeyboardRequested?.Invoke());
        OpenDriverPage = new Command(() => _services.Open(SetupViewModel.DriverPage));
        Restart = new Command(() => _services.Restart(), () => !_workspace.ProfileUnsaved);
        _services.Session.StateChanged += _ => _services.Post(OnSessionState);
        _workspace.PropertyChanged += OnWorkspaceChanged;
        game.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameViewModel.Warning) or nameof(GameViewModel.Hint))
            {
                Refresh();
            }
        };
        Refresh();
    }

    /// <summary>The window shows the calibration page.</summary>
    public event Action? CalibrationRequested;

    /// <summary>The window shows the keyboard page.</summary>
    public event Action? KeyboardRequested;

    public string StateText => _status.State switch
    {
        SessionState.Starting => "Starting",
        SessionState.Running when !_status.GameRunning => "Mapping, waiting for the game",
        // The app leaves such a game alone even in front, so waiting would not help. The warning says what will.
        SessionState.Running when _gameElevation == Elevation.Elevated => "Mapping, but the game runs as administrator",
        SessionState.Running when _gameElevation == Elevation.Unknown => "Mapping, but the game may run as administrator",
        // The controller moves only while the game is in front.
        SessionState.Running when !_status.GameHasFocus => "Mapping, game in background",
        SessionState.Running => "Mapping",
        SessionState.Paused => "Paused, keyboard unplugged",
        SessionState.Stopping => "Stopping",
        _ when _status.RestartRequired || _blocker is not null => _blockerTitle,
        _ when _startFailure is not null => "Couldn't start mapping",
        _ => "Ready",
    };

    public bool IsMapping => _status.State is SessionState.Running or SessionState.Paused;

    public StatusTone Tone => _status.State switch
    {
        SessionState.Running when _status.GameRunning && _gameElevation != Elevation.Visible => StatusTone.NotReady,
        SessionState.Running => StatusTone.Mapping,
        SessionState.Paused => StatusTone.Paused,
        SessionState.Starting or SessionState.Stopping => StatusTone.Busy,
        _ when _status.RestartRequired || _blocker is not null || _startFailure is not null => StatusTone.NotReady,
        _ => StatusTone.Ready,
    };

    /// <summary>
    /// While stopped: why the last session ended or the last start failed, or that Start
    /// is ready. A stop the user asked for needs no reason. Hidden while a restart is
    /// required: the end message would say to press Start, and the blocker says why not.
    /// </summary>
    public string? Reason
    {
        get
        {
            if (_status.State == SessionState.Paused)
            {
                return "Plug the same keyboard back in and mapping picks up again.";
            }
            if (_status.State == SessionState.Running && !_status.GameRunning)
            {
                return "Launch the game. The controller starts moving once it is in front.";
            }
            // Where someone lands after switching away from a game that missed the controller.
            if (_status.State == SessionState.Running && _status.GameRunning && !_status.GameHasFocus && _gameElevation == Elevation.Visible)
            {
                return "Switch to the game. If it does not see the controller, press Stop, close the game, press Start, then open the game again.";
            }
            if (_status.State != SessionState.Idle || _status.RestartRequired)
            {
                return null;
            }
            var failure = _startFailure ?? (_status.LastEnd is { Reason: not EndReason.UserStop } end ? end.Message : null);
            var reason = failure is not null ? (CanStart ? failure : WithoutRetry(failure))
                : CanStart ? "Press Start, then launch or switch to the game."
                : null;
            // Start compiles the saved profile, not the editor's.
            return CanStart && _workspace.ProfileUnsaved
                ? $"{reason} Start uses your saved bindings. To use your changes, save them in Edit profiles.".TrimStart()
                : reason;
        }
    }

    /// <summary>A session's end without its last sentence when that one says to press Start, which the blocker below it rules out for now.</summary>
    private static string WithoutRetry(string message) =>
        message.IndexOf(" Press Start", StringComparison.Ordinal) is var at and >= 0 ? message[..at] : message;

    /// <summary>Why Start is unavailable, or null.</summary>
    public string? Blocker
    {
        get => _blocker;
        private set => Set(ref _blocker, value);
    }

    /// <summary>Start waits for calibration; the card offers a way there.</summary>
    public bool BlockedByCalibration
    {
        get => _blockedByCalibration;
        private set => Set(ref _blockedByCalibration, value);
    }

    /// <summary>Start waits for something on the keyboard page; the card offers a way there.</summary>
    public bool BlockedByKeyboard
    {
        get => _blockedByKeyboard;
        private set => Set(ref _blockedByKeyboard, value);
    }

    /// <summary>Start waits for the controller driver; the card offers its release page.</summary>
    public bool BlockedByDriver
    {
        get => _blockedByDriver;
        private set => Set(ref _blockedByDriver, value);
    }

    public bool CanStart => !_workspace.SessionActive && _blocker is null;

    /// <summary>Stop is the button that matters while a session is up.</summary>
    public bool CanStop => _workspace.SessionActive;

    /// <summary>Sensor cycle and controller updates while mapping (A12).</summary>
    public string? Timing
    {
        get => _timing;
        private set => Set(ref _timing, value);
    }

    /// <summary>Changed in place, so a warning that stays keeps its live region and is not read out again.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>What the app handled itself or that works as designed, so nothing needs doing. Changed in place like the warnings.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>A controller could not be removed; only a restart clears it.</summary>
    public bool RestartRequired => _status.RestartRequired;

    public Command Start { get; }

    public Command Stop { get; }

    public Command GoToCalibration { get; }

    public Command GoToKeyboard { get; }

    public Command OpenDriverPage { get; }

    public Command Restart { get; }

    internal async Task StartAsync()
    {
        Refresh();
        if (!CanStart || _workspace.Board is not { } board || Compile(out _) is not { } compiled)
        {
            return;
        }
        _startFailure = null;
        var profile = _workspace.ActiveProfile!;
        var request = new SessionRequest(board.Id, _workspace.GamePath!, compiled, _workspace.Calibration?.Signatures);
        _mappedKeys = [.. profile.AllKeys()];
        _otherKeyboardSeen = false;
        _workspace.RunningProfileText = ProfileJson.Serialize(profile);
        // Before the session opens the board: the live sensor stops on this.
        _workspace.Session = SessionState.Starting;
        _services.Log($"Start: profile \"{profile.Id}\", game {Path.GetFileName(_workspace.GamePath)}, keyboard {board.Info.Name}.");
        try
        {
            if (await _services.Session.StartAsync(request) is { } refused)
            {
                _startFailure = refused.Message;
                _services.Log("Start failed: " + refused.Message);
            }
        }
        catch (Exception e)
        {
            // The session unwinds a failed start itself; the card must not stay on Starting.
            _startFailure = "Mapping could not start because of an unexpected error. Press Start to try again.";
            _services.Log("Start failed: " + e);
        }
        OnSessionState();
    }

    internal async Task StopAsync()
    {
        await _services.Session.StopAsync(EndReason.UserStop);
        OnSessionState();
    }

    /// <summary>Reads the session's status again. Called by the window's timer every <see cref="RefreshMs"/>.</summary>
    public void Tick(long nowMs)
    {
        Refresh(nowMs);
        if (_status.State == SessionState.Running)
        {
            if (_rateFrom is { } from && nowMs - from.AtMs >= 1000)
            {
                _rate = (_status.SubmitCount - from.Count) * 1000.0 / (nowMs - from.AtMs);
                _rateFrom = (_status.SubmitCount, nowMs);
            }
            _rateFrom ??= (_status.SubmitCount, nowMs);
        }
        else
        {
            _rateFrom = null;
            _rate = 0;
        }
        Timing = _status.State == SessionState.Running && float.IsFinite(_status.CycleP50Ms)
            ? string.Create(CultureInfo.CurrentCulture, $"Keyboard read every {_status.CycleP50Ms:0.0} ms, and 99% of reads within {_status.CycleP99Ms:0.0} ms.")
                // The pad goes on sending at rest without the game, so the rate means something only with it in front.
                + (_status.GameHasFocus ? string.Create(CultureInfo.CurrentCulture, $" Controller updated {_rate:0} times a second.") : "")
            : null;
    }

    /// <summary>B11: a mapped key from another keyboard is blocked too; the notice shows once per run of the app.</summary>
    public void OnKey(in RawKeyEvent key)
    {
        if (_otherKeyboardShown || !IsMapping || !key.Down || key.Device == 0 || !_mappedKeys.Contains(key.Code)
            || _workspace.Board is not { } board || _services.KeyEvents.ContainerOf(key.Device) is not { } container || container == board.Id)
        {
            return;
        }
        _otherKeyboardSeen = true;
        _otherKeyboardShown = true;
        Refresh();
    }

    /// <summary>The session's state changed; read it rather than trust the posted value, which may be behind.</summary>
    private void OnSessionState()
    {
        var state = _services.Session.State;
        _workspace.Session = state;
        if (state == SessionState.Idle)
        {
            _workspace.RunningProfileText = null;
            _otherKeyboardSeen = false;
            _logged.Clear();
            _atLimitSince.Clear();
            _stuckAtLimit = [];
        }
        if (state != _loggedState)
        {
            _loggedState = state;
            _services.Log(state == SessionState.Idle && _services.Session.LastEnd is { } end
                ? $"Session ended ({end.Reason}): {end.Message}"
                : $"Session {state}.");
        }
        Refresh();
    }

    /// <summary>Anything the card reads may have changed: the board, the game, the profile, the calibration, the driver.</summary>
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>Reads the session's status and recomputes the card. Only the timer passes the time, which moves the at-limit clocks.</summary>
    private void Refresh(long? nowMs = null)
    {
        _status = _services.Session.Status();
        // The session sees the game's elevation only while it is in front, so the last one seen
        // holds until the game has focus again or its process is gone. Until the session has
        // seen it once, the game list's reading stands in, so Start does not hide a known cause.
        if (_status.State != SessionState.Running || !_status.GameRunning)
        {
            _gameElevation = Elevation.Visible;
            _gameSeen = false;
        }
        else if (_status.GameElevation != Elevation.Visible || _status.GameHasFocus)
        {
            _gameElevation = _status.GameElevation;
            _gameSeen = true;
        }
        else if (!_gameSeen)
        {
            _gameElevation = _workspace.GameElevation;
        }
        if (nowMs is { } now)
        {
            TrackKeysAtLimit(now);
        }
        if (_workspace.Calibration is { } calibration)
        {
            foreach (var (key, was) in _recalibrate.ToList())
            {
                if (!Equals(calibration.Keys.GetValueOrDefault(key), was))
                {
                    _recalibrate.Remove(key);
                }
            }
        }
        var (blocker, title, fix) = _workspace.SessionActive ? (null, "Not ready", Fix.None) : FindBlocker();
        _blockerTitle = title;
        Blocker = blocker;
        BlockedByCalibration = fix == Fix.Calibration;
        BlockedByDriver = fix == Fix.Driver;
        BlockedByKeyboard = fix == Fix.Keyboard;
        var (warnings, notes) = FindWarnings();
        var lines = warnings.Concat(notes).Select(warning => "Warning: " + warning);
        if (IsMapping && _status.SensorProblem is { } problem)
        {
            lines = lines.Append("Sensor fault: " + problem);
        }
        foreach (var line in lines.Where(_logged.Add))
        {
            _services.Log(line);
        }
        // The chosen game's notices sit with the card's own, after logging, since they name the game.
        if (_game.Warning is { } gameWarning)
        {
            warnings.Add(gameWarning);
        }
        if (_game.Hint is { } hint)
        {
            notes.Add(hint);
        }
        Sync(Warnings, warnings);
        Sync(Notes, notes);
        foreach (var name in new[] { nameof(StateText), nameof(Tone), nameof(IsMapping), nameof(Reason), nameof(CanStart), nameof(CanStop), nameof(RestartRequired) })
        {
            Raise(name);
        }
        Start.Refresh();
        Stop.Refresh();
        Restart.Refresh();
    }

    /// <summary>A16: keys that have read the sensor's limit for <see cref="AtLimitWarningMs"/>, restarting a key's clock whenever it leaves the limit.</summary>
    private void TrackKeysAtLimit(long nowMs)
    {
        var atLimit = _status.KeysAtLimit ?? [];
        foreach (var key in _atLimitSince.Keys.Except(atLimit).ToList())
        {
            _atLimitSince.Remove(key);
        }
        foreach (var key in atLimit)
        {
            _atLimitSince.TryAdd(key, nowMs);
        }
        _stuckAtLimit = [.. _atLimitSince.Where(k => nowMs - k.Value >= AtLimitWarningMs).Select(k => k.Key)];
        foreach (var key in _stuckAtLimit)
        {
            _recalibrate.TryAdd(key, _workspace.Calibration?.Keys.GetValueOrDefault(key));
        }
    }

    private enum Fix
    {
        None,
        Calibration,
        Driver,
        Keyboard,
    }

    /// <summary>The first thing Start waits for, in the order a new user meets them: the driver before anything they could spend time on.</summary>
    /// <summary>Why Start is unavailable, a short title for the status card that names it, and the fix the card offers.</summary>
    private (string?, string, Fix) FindBlocker()
    {
        if (_status.RestartRequired)
        {
            var message = SessionEnd.For(EndReason.RestartRequired).Message;
            return (_workspace.ProfileUnsaved ? message + " Save or discard your profile changes in Edit profiles first." : message, "Restart needed", Fix.None);
        }
        switch (_workspace.Driver)
        {
            case DriverState.Missing:
                return ($"Install the ViGEmBus controller driver first. Get version {SetupViewModel.DriverVersion} from its release page, run the installer, then come back to this window.", "Controller driver not installed", Fix.Driver);
            case DriverState.NotStarted:
                return (SessionEnd.For(EndReason.DriverNotStarted).Message, "Controller driver not running", Fix.None);
        }
        if (_workspace.Board is not { } board)
        {
            return _workspace.ReadingFirmware ? ("Reading the keyboard's firmware.", "Reading the keyboard", Fix.None)
                : _services.Keyboards.Current.Any(k => k.Known) ? ("Choose your keyboard.", "Choose your keyboard", Fix.Keyboard)
                : ("Plug in your Apex Pro keyboard.", "Keyboard not plugged in", Fix.None);
        }
        if (board.Firmware.Version is null)
        {
            return ("The keyboard's firmware could not be read. " + board.Firmware.Problem + " Unplug the keyboard and plug it back in to try again.", "Keyboard firmware not read", Fix.None);
        }
        if (board.Unsupported)
        {
            return _services.Keyboards.Current.Count(k => k.Known) > 1
                ? ("The app cannot read this keyboard's sensors. Choose another keyboard on the Keyboard page.", "Keyboard not supported", Fix.Keyboard)
                : ("The app cannot read this keyboard's sensors. Plug in an Apex Pro the app supports.", "Keyboard not supported", Fix.None);
        }
        if (!board.CanReadSensors)
        {
            return ("Check its sensors first, under Try this keyboard on the Keyboard page.", "Keyboard not tested yet", Fix.Keyboard);
        }
        if (_workspace.GamePath is null)
        {
            return ("Launch your game, then choose it in the Game row below.", "Choose your game", Fix.None);
        }
        if (_workspace.ActiveProfile is null)
        {
            return ("The active profile could not be loaded. Choose a different profile in the Profile row below.", "Profile could not be loaded", Fix.None);
        }
        if (Compile(out var uncalibrated) is null)
        {
            return ($"Calibrate {Wording.List([.. uncalibrated.Select(_services.KeyName)])} first.", "Keys need calibrating", Fix.Calibration);
        }
        return (_services.Session.WhyNotStartable(board.Id)?.Message, "Not ready", Fix.None);
    }

    /// <summary>The active profile against the chosen board's calibration; null with the keys still to calibrate (F7).</summary>
    private CompiledProfile? Compile(out IReadOnlyList<ScanCode> uncalibrated)
    {
        uncalibrated = [];
        return _workspace.ActiveProfile is { } profile
            ? CompiledProfile.TryCompile(profile, _workspace.Map, _workspace.Calibration?.Keys ?? new Dictionary<ScanCode, KeyCalibration>(), out uncalibrated)
            : null;
    }

    private static void Sync(ObservableCollection<string> shown, List<string> next)
    {
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!next.Contains(shown[i]))
            {
                shown.RemoveAt(i);
            }
        }
        for (var i = 0; i < next.Count; i++)
        {
            var at = shown.IndexOf(next[i]);
            if (at < 0)
            {
                shown.Insert(i, next[i]);
            }
            else if (at != i)
            {
                shown.Move(at, i);
            }
        }
    }

    private (List<string> Warnings, List<string> Notes) FindWarnings()
    {
        var warnings = new List<string>();
        var notes = new List<string>();
        var status = _status;
        if (IsMapping)
        {
            if (status.FallbackKeys > 0)
            {
                var one = status.FallbackKeys == 1;
                warnings.Add($"{status.FallbackKeys} analog {(one ? "key is" : "keys are")} working as {(one ? "an on/off key" : "on/off keys")} because {(one ? "its" : "their")} readings are late or missing. " +
                    $"{(one ? "It turns" : "They turn")} analog again when the readings return. If this lasts, stop mapping, then unplug the keyboard and plug it back in.");
            }
            if (_stuckAtLimit.Count > 0)
            {
                var names = Wording.List([.. _stuckAtLimit.Select(_services.KeyName)]);
                var one = _stuckAtLimit.Count == 1;
                warnings.Add($"{names} {(one ? "presses" : "press")} deeper than when {(one ? "it was" : "they were")} calibrated, so {(one ? "it reaches" : "they reach")} full output early. " +
                    $"Stop mapping and calibrate {names} again.");
            }
            if (Wording.RunAsAdministrator(_gameElevation, "the game") is { } elevation)
            {
                warnings.Add(elevation);
            }
            if (status.KeysAwaitingRelease)
            {
                notes.Add("A key that was already down stays off until you release it once.");
            }
            if (status.HookReinstalls > 0)
            {
                notes.Add(status.HookReinstalls == 1
                    ? "Windows turned off key blocking once, and the app turned it back on."
                    : $"Windows turned off key blocking {status.HookReinstalls} times, and the app turned it back on each time.");
            }
            if (_otherKeyboardSeen)
            {
                notes.Add("A mapped key was pressed on another keyboard. While mapping, mapped keys are blocked on every keyboard, not just the Apex Pro.");
            }
        }
        else if (_recalibrate.Count > 0)
        {
            var names = Wording.List([.. _recalibrate.Keys.Select(_services.KeyName)]);
            var one = _recalibrate.Count == 1;
            warnings.Add($"While mapping, {names} pressed deeper than when {(one ? "it was" : "they were")} calibrated. Calibrate {names} again.");
        }
        if (_workspace.Board is { Verified: false, CanReadSensors: true } board)
        {
            warnings.Add($"The {board.Info.Name} is not on the tested list yet. If mapping acts oddly, stop it and report it on GitHub.");
        }
        if (_workspace.SettingsProblem is { } settings)
        {
            warnings.Add(settings);
        }
        return (warnings, notes);
    }
}
