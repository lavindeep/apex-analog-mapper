using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;

namespace ApexMapper.Windows.Session;

public enum SessionState
{
    Idle,
    Starting,
    Running,

    /// <summary>The keyboard is unplugged: pad neutral, hook still installed, resumes on reconnect.</summary>
    Paused,

    Stopping,
}

/// <summary>Why a session is not running: the cause of the last stop, or why a start was refused or failed.</summary>
public enum EndReason
{
    UserStop,
    Hotkey,
    AppClosing,
    ProfileEdited,
    GameExited,
    ControllerDisconnected,
    SleepOrWake,
    EngineStalled,
    EngineFault,
    HookFailed,
    SafetyFault,
    KeyboardMissing,
    RestartRequired,
    DriverMissing,
    DriverNotStarted,
    PadFailed,
    StartFailed,
}

/// <param name="Message">A sentence for the status card.</param>
public sealed record SessionEnd(EndReason Reason, string Message)
{
    public static SessionEnd For(EndReason reason) => new(reason, reason switch
    {
        EndReason.UserStop => "Stopped.",
        EndReason.Hotkey => "Stopped with Ctrl+Left Alt+F12.",
        EndReason.AppClosing => "Stopped because the app closed.",
        EndReason.ProfileEdited => "Stopped because the active profile was edited.",
        EndReason.GameExited => "Stopped because the game exited.",
        EndReason.ControllerDisconnected => "Stopped because the virtual controller disappeared.",
        EndReason.SleepOrWake => "Stopped because the PC went to sleep. Press Start to map again.",
        EndReason.EngineStalled => "Stopped because the app stopped updating the controller for a moment. Press Start to try again.",
        EndReason.EngineFault => "Stopped because of an internal error. Press Start to try again.",
        EndReason.HookFailed => "Stopped because Windows turned off key blocking and it could not be turned back on. Press Start to try again.",
        EndReason.SafetyFault => "Stopped because one of the app's safety checks stopped working.",
        EndReason.KeyboardMissing => "The selected keyboard is not connected.",
        EndReason.RestartRequired => "The last virtual controller could not be removed. Restart the app to clear it, then press Start.",
        EndReason.DriverMissing => "The ViGEmBus driver is not installed.",
        EndReason.DriverNotStarted => "The ViGEmBus driver is installed but not running. Restart the PC to finish installing it.",
        EndReason.PadFailed => "The virtual controller could not be connected.",
        _ => "The session could not start.",
    });
}

/// <summary>What to map: the keyboard, the game (running or not yet), and a profile already compiled against that keyboard's calibration.</summary>
/// <param name="Keyboard">Container id of the selected keyboard.</param>
/// <param name="GamePath">Full path of the game's executable.</param>
/// <param name="Signatures">Group signatures recorded at calibration, by group; null or missing groups skip the check.</param>
public sealed record SessionRequest(
    Guid Keyboard,
    string GamePath,
    CompiledProfile Profile,
    IReadOnlyDictionary<int, GroupSignature>? Signatures = null);

/// <summary>A reading for the status card, taken on any thread.</summary>
/// <param name="GameRunning">The game's process has been found; until then the session is up and waiting for it.</param>
/// <param name="GameElevation">Whether the game in front runs where the hook cannot see it: elevated while this process is not, or with a token that could not be read. <see cref="Elevation.Visible"/> when the game is not in front.</param>
/// <param name="FallbackKeys">Analog keys driven from the keyboard's on/off state right now.</param>
/// <param name="SensorProblem">Why, while <paramref name="FallbackKeys"/> is above zero.</param>
/// <param name="KeysAwaitingRelease">The game has focus and a mapped key stays dead until it is released once.</param>
/// <param name="SubmitCount">Reports the driver accepted this session, for the submit rate.</param>
/// <param name="HookReinstalls">Times Windows removed the hook and the session put it back.</param>
/// <param name="RestartRequired">A controller could not be removed; Start is refused until the app restarts.</param>
/// <param name="CycleP50Ms">Median sensor cycle period over the last hundred cycles, or NaN before the first.</param>
/// <param name="CycleP99Ms">99th percentile of the same, or NaN.</param>
/// <param name="KeysAtLimit">Analog keys calibrated short of the sensor's limit that read at it now, or null. One that stays there is implausible (A16).</param>
public sealed record SessionStatus(
    SessionState State,
    SessionEnd? LastEnd,
    bool GameRunning,
    bool GameHasFocus,
    Elevation GameElevation,
    int FallbackKeys,
    string? SensorProblem,
    bool KeysAwaitingRelease,
    long SubmitCount,
    int HookReinstalls,
    bool RestartRequired,
    float CycleP50Ms = float.NaN,
    float CycleP99Ms = float.NaN,
    IReadOnlyList<ScanCode>? KeysAtLimit = null);

/// <summary>What the window uses of a <see cref="MappingSession"/>, so its view models can be tested without one.</summary>
public interface IMappingSession
{
    /// <inheritdoc cref="MappingSession.StateChanged"/>
    event Action<SessionState>? StateChanged;

    SessionState State { get; }

    SessionEnd? LastEnd { get; }

    bool RestartRequired { get; }

    SessionEnd? WhyNotStartable(Guid keyboard);

    Task<SessionEnd?> StartAsync(SessionRequest request);

    Task StopAsync(EndReason reason);

    /// <inheritdoc cref="MappingSession.Status"/>
    SessionStatus Status();
}

/// <summary>
/// The app-lifetime services a session uses, and the factories for its parts. The
/// defaults are the real ones; tests replace what they cannot drive.
/// </summary>
public sealed class SessionServices
{
    /// <summary>Connected boards; the session pauses when the selected one disappears.</summary>
    public required KeyboardDiscovery Keyboards { get; init; }

    /// <summary>The app's Raw Input pump: its newest event time, for noticing a lost hook, and whether it still runs.</summary>
    public required IRawInputActivity RawInput { get; init; }

    public required IPowerEvents Power { get; init; }

    public Func<DriverState> DriverState { get; init; } = DriverStatus.Query;

    public Func<CancellationToken, VirtualPad> ConnectPad { get; init; } = VirtualPad.Connect;

    public Func<string, IGameProcess?> FindGame { get; init; } = GameProcess.Find;

    public Func<ForegroundFlag, IForegroundSource> CreateForeground { get; init; } = flag => new ForegroundTracker(flag);

    public Func<Guid, SensorSnapshot, PollerConfig, SensorPoller> CreatePoller { get; init; } = SensorPoller.ForKeyboard;

    /// <summary>How long after the game's process exits the session looks for it again before stopping.</summary>
    public TimeSpan GameRelaunchGrace { get; init; } = TimeSpan.FromMilliseconds(MappingSession.GameRelaunchGraceMs);

    /// <summary>Test mode: the hook treats injected keys as physical ones.</summary>
    public bool SwallowInjected { get; init; }

    /// <summary>
    /// Unit tests: the session's hook runs detached, never installed into Windows and
    /// never reading the real keyboard, so a test cannot swallow or see anyone's keys.
    /// </summary>
    internal bool DetachedHook { get; init; }
}
