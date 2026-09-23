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
using ApexMapper.Windows.Session;

namespace ApexMapper.App.ViewModels;

/// <summary>
/// The status card: the session's state and why it last stopped, Start and Stop, why
/// Start is unavailable with a way to the calibration card when that is the reason,
/// the sensor cycle and controller update rate while mapping, and the warnings. Session
/// events arrive on the session thread and are posted here; the card also reads the
/// session's status every <see cref="RefreshMs"/>. It logs state changes and why a
/// session ended, never keys.
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
        SessionState.Running => "Mapping",
        SessionState.Paused => "Paused: the keyboard is unplugged",
        SessionState.Stopping => "Stopping",
        _ => "Stopped",
    };

    public bool IsMapping => _status.State is SessionState.Running or SessionState.Paused;

    /// <summary>Why the last session ended or the last start failed, while stopped.</summary>
    public string? Reason => _status.State == SessionState.Idle ? _status.LastEnd?.Message : null;

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

    public bool CanStart => !_workspace.SessionActive && _blocker is null;

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
            ? string.Create(CultureInfo.CurrentCulture, $"Sensor cycle {_status.CycleP50Ms:0.0} ms median, {_status.CycleP99Ms:0.0} ms p99. Controller updates: {_rate:0} a second.")
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

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Workspace.SessionActive) or nameof(Workspace.SettingsProblem))
        {
            Refresh();
        }
    }

    /// <summary>Reads the session's status and recomputes the card. Only the timer passes the time, which moves the at-limit clocks.</summary>
    private void Refresh(long? nowMs = null)
    {
        _status = _services.Session.Status();
        if (nowMs is { } now)
        {
            TrackKeysAtLimit(now);
        }
        var (blocker, calibration) = _workspace.SessionActive ? (null, false) : FindBlocker();
        Blocker = blocker;
        BlockedByCalibration = calibration;
        Warnings = FindWarnings();
        foreach (var name in new[] { nameof(StateText), nameof(IsMapping), nameof(Reason), nameof(CanStart), nameof(RestartRequired) })
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

    private (string?, bool) FindBlocker()
    {
        if (_status.RestartRequired)
        {
            return (SessionEnd.For(EndReason.RestartRequired).Message, false);
        }
        if (_workspace.Board is not { } board)
        {
            return ("Choose a keyboard that is plugged in.", false);
        }
        if (board.Firmware.Version is null)
        {
            return ("The keyboard's firmware could not be read. " + board.Firmware.Problem, false);
        }
        if (!board.CanReadSensors)
        {
            return ("This keyboard has not been tested. Try it on the keyboard card first.", false);
        }
        if (_workspace.GamePath is null)
        {
            return ("Choose the game.", false);
        }
        if (_workspace.ActiveProfile is null)
        {
            return ("The active profile could not be loaded.", false);
        }
        if (Compile(out var uncalibrated) is null)
        {
            return ($"Calibrate {Wording.List([.. uncalibrated.Select(_services.KeyName)])} first.", true);
        }
        return (_services.Session.WhyNotStartable(board.Id)?.Message, false);
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
        if (status.RestartRequired)
        {
            warnings.Add("The virtual controller from the last session could not be removed and may still hold its last input. Restart the app to clear it.");
        }
        if (IsMapping)
        {
            if (status.FallbackKeys > 0)
            {
                warnings.Add($"{status.FallbackKeys} analog {(status.FallbackKeys == 1 ? "key follows" : "keys follow")} the keyboard's on and off state instead of its depth. {status.SensorProblem}");
            }
            if (_stuckAtLimit.Count > 0)
            {
                var one = _stuckAtLimit.Count == 1;
                warnings.Add($"{Wording.List([.. _stuckAtLimit.Select(_services.KeyName)])} {(one ? "reads" : "read")} the sensor's maximum, past the full press {(one ? "it was" : "they were")} " +
                    $"calibrated with, so {(one ? "it reaches" : "they reach")} full output early. Calibrate {(one ? "it" : "them")} again.");
            }
            if (!status.GameRunning)
            {
                warnings.Add("Waiting for the game to start.");
            }
            if (status.GameElevated)
            {
                warnings.Add("The game runs as administrator, so the mapper cannot see its keys. Close the mapper and run it as administrator.");
            }
            if (status.KeysAwaitingRelease)
            {
                warnings.Add("A key held while the game came back to the front stays off until you release it once.");
            }
            if (status.HookReinstalls > 0)
            {
                warnings.Add($"Windows removed the keyboard hook {status.HookReinstalls} {(status.HookReinstalls == 1 ? "time" : "times")}, and it was put back.");
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
