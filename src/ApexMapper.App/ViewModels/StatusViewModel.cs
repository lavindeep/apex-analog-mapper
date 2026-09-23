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
    private string? _blocker;
    private bool _blockedByCalibration;
    private bool _blockedByDriver;
    /// <summary>The warnings and sensor faults logged since the last session ended: each is logged once a session.</summary>
    private readonly HashSet<string> _logged = [];
    private string? _timing;
    private IReadOnlyList<string> _warnings = [];
    private HashSet<ScanCode> _mappedKeys = [];
    private bool _otherKeyboardSeen;
    private bool _otherKeyboardShown;
    private (long Count, long AtMs)? _rateFrom;
    private double _rate;
    private SessionState _loggedState;
    private readonly Dictionary<ScanCode, long> _atLimitSince = [];
    private List<ScanCode> _stuckAtLimit = [];

    public StatusViewModel(AppServices services, Workspace workspace)
    {
        _services = services;
        _workspace = workspace;
        _status = services.Session.Status();
        Start = new Command(() => _ = StartAsync(), () => CanStart);
        Stop = new Command(() => _ = StopAsync(), () => _workspace.SessionActive);
        GoToCalibration = new Command(() => CalibrationRequested?.Invoke());
        OpenDriverPage = new Command(() => _services.Open(SetupViewModel.DriverPage));
        Restart = new Command(() => _services.Restart());
        _services.Session.StateChanged += _ => _services.Post(OnSessionState);
        _workspace.PropertyChanged += OnWorkspaceChanged;
        Refresh();
    }

    /// <summary>The window scrolls to the calibration card and opens it.</summary>
    public event Action? CalibrationRequested;

    public string StateText => _status.State switch
    {
        SessionState.Starting => "Starting",
        SessionState.Running when !_status.GameRunning => "Mapping, waiting for the game",
        SessionState.Running => "Mapping",
        SessionState.Paused => "Paused: the keyboard is unplugged",
        SessionState.Stopping => "Stopping",
        _ => "Stopped",
    };

    public bool IsMapping => _status.State is SessionState.Running or SessionState.Paused;

    /// <summary>
    /// While stopped: why the last session ended or the last start failed, or that Start
    /// is ready. A stop the user asked for needs no reason. Hidden while a restart is
    /// required: the end message would say to press Start, and the blocker says why not.
    /// </summary>
    public string? Reason => _status.State != SessionState.Idle || _status.RestartRequired ? null
        : _status.LastEnd is { Reason: not EndReason.UserStop } end ? end.Message
        : CanStart ? "Ready. Press Start. Mapping begins once the game is in front."
        : null;

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

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set
        {
            if (!value.SequenceEqual(_warnings))
            {
                Set(ref _warnings, value);
            }
        }
    }

    /// <summary>A controller could not be removed; only a restart clears it.</summary>
    public bool RestartRequired => _status.RestartRequired;

    public Command Start { get; }

    public Command Stop { get; }

    public Command GoToCalibration { get; }

    public Command OpenDriverPage { get; }

    public Command Restart { get; }

    internal async Task StartAsync()
    {
        Refresh();
        if (!CanStart || _workspace.Board is not { } board || Compile(out _) is not { } compiled)
        {
            return;
        }
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
                _services.Log("Start failed: " + refused.Message);
            }
        }
        catch (Exception e)
        {
            // The session unwinds a failed start itself; the card must not stay on Starting.
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
        Timing = IsMapping && float.IsFinite(_status.CycleP50Ms)
            ? string.Create(CultureInfo.CurrentCulture, $"Keyboard read every {_status.CycleP50Ms:0.0} ms ({_status.CycleP99Ms:0.0} ms for the slowest 1%). Controller updated {_rate:0} times a second.")
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
        if (nowMs is { } now)
        {
            TrackKeysAtLimit(now);
        }
        var (blocker, fix) = _workspace.SessionActive ? (null, Fix.None) : FindBlocker();
        Blocker = blocker;
        BlockedByCalibration = fix == Fix.Calibration;
        BlockedByDriver = fix == Fix.Driver;
        var warnings = FindWarnings();
        var lines = warnings.Select(warning => "Warning: " + warning);
        if (IsMapping && _status.SensorProblem is { } problem)
        {
            lines = lines.Append("Sensor fault: " + problem);
        }
        foreach (var line in lines.Where(_logged.Add))
        {
            _services.Log(line);
        }
        Warnings = warnings;
        foreach (var name in new[] { nameof(StateText), nameof(IsMapping), nameof(Reason), nameof(CanStart), nameof(CanStop), nameof(RestartRequired) })
        {
            Raise(name);
        }
        Start.Refresh();
        Stop.Refresh();
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
    }

    private enum Fix
    {
        None,
        Calibration,
        Driver,
    }

    /// <summary>The first thing Start waits for, in the order a new user meets them: the driver before anything they could spend time on.</summary>
    private (string?, Fix) FindBlocker()
    {
        if (_status.RestartRequired)
        {
            return (SessionEnd.For(EndReason.RestartRequired).Message, Fix.None);
        }
        switch (_workspace.Driver)
        {
            case DriverState.Missing:
                return ("Install the ViGEmBus controller driver first.", Fix.Driver);
            case DriverState.NotStarted:
                return (SessionEnd.For(EndReason.DriverNotStarted).Message, Fix.None);
        }
        if (_workspace.Board is not { } board)
        {
            return (_workspace.ReadingFirmware ? "Reading the keyboard's firmware."
                : _services.Keyboards.Current.Any(k => k.Known) ? "Choose your keyboard on the keyboard card."
                : "Plug in your Apex Pro keyboard.", Fix.None);
        }
        if (board.Firmware.Version is null)
        {
            return ("The keyboard's firmware could not be read. " + board.Firmware.Problem, Fix.None);
        }
        if (!board.CanReadSensors)
        {
            return ("This keyboard has not been tested. Try it on the keyboard card first.", Fix.None);
        }
        if (_workspace.GamePath is null)
        {
            return ("Choose the game.", Fix.None);
        }
        if (_workspace.ActiveProfile is null)
        {
            return ("The active profile could not be loaded.", Fix.None);
        }
        if (Compile(out var uncalibrated) is null)
        {
            return ($"Calibrate {Wording.List([.. uncalibrated.Select(_services.KeyName)])} first.", Fix.Calibration);
        }
        return (_services.Session.WhyNotStartable(board.Id)?.Message, Fix.None);
    }

    /// <summary>The active profile against the chosen board's calibration; null with the keys still to calibrate (F7).</summary>
    private CompiledProfile? Compile(out IReadOnlyList<ScanCode> uncalibrated)
    {
        uncalibrated = [];
        return _workspace.ActiveProfile is { } profile
            ? CompiledProfile.TryCompile(profile, _workspace.Map, _workspace.Calibration?.Keys ?? new Dictionary<ScanCode, KeyCalibration>(), out uncalibrated)
            : null;
    }

    private List<string> FindWarnings()
    {
        var warnings = new List<string>();
        var status = _status;
        if (IsMapping)
        {
            if (status.FallbackKeys > 0)
            {
                var one = status.FallbackKeys == 1;
                warnings.Add($"{status.FallbackKeys} analog {(one ? "key is" : "keys are")} on or off only, because the keyboard stopped sending how far keys are pressed. " +
                    $"{(one ? "It goes" : "They go")} back to analog when readings return. If this lasts, stop mapping, then unplug the keyboard and plug it back in.");
            }
            if (_stuckAtLimit.Count > 0)
            {
                var names = Wording.List([.. _stuckAtLimit.Select(_services.KeyName)]);
                var one = _stuckAtLimit.Count == 1;
                warnings.Add($"{names} {(one ? "presses" : "press")} deeper than when {(one ? "it was" : "they were")} calibrated, so {(one ? "it reaches" : "they reach")} full output early. " +
                    $"Stop mapping and calibrate {names} again.");
            }
            if (Wording.RunAsAdministrator(status.GameElevation, "the game") is { } elevation)
            {
                warnings.Add(elevation);
            }
            if (status.KeysAwaitingRelease)
            {
                warnings.Add("A key held while the game came back to the front stays off until you release it once.");
            }
            if (status.HookReinstalls > 0)
            {
                warnings.Add(status.HookReinstalls == 1
                    ? "Windows turned off key blocking once, and the app turned it back on."
                    : $"Windows turned off key blocking {status.HookReinstalls} times, and the app turned it back on each time.");
            }
            if (_otherKeyboardSeen)
            {
                warnings.Add("A mapped key was pressed on another keyboard. While mapping, mapped keys are blocked on every keyboard, not just the Apex Pro.");
            }
        }
        if (_workspace.Board is { Verified: false, CanReadSensors: true } board)
        {
            warnings.Add($"The {board.Info.Name} has not been tested with this app.");
        }
        if (_workspace.SettingsProblem is { } settings)
        {
            warnings.Add(settings);
        }
        return warnings;
    }
}
