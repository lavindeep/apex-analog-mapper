using ApexMapper.Core.Engine;
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
    KeyboardMissing,
    GameNotRunning,
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
        EndReason.Hotkey => "Stopped with Ctrl+Alt+F12.",
        EndReason.AppClosing => "Stopped because the app closed.",
        EndReason.ProfileEdited => "Stopped because the active profile was edited.",
        EndReason.GameExited => "Stopped because the game exited.",
        EndReason.ControllerDisconnected => "Stopped because the virtual controller disappeared.",
        EndReason.SleepOrWake => "Stopped because the PC went to sleep. Press Start to map again.",
        EndReason.EngineStalled => "Stopped because the mapper stopped responding. The controller was unplugged as a precaution.",
        EndReason.EngineFault => "Stopped because the engine failed.",
        EndReason.HookFailed => "Stopped because Windows removed the keyboard hook and it could not be put back.",
        EndReason.KeyboardMissing => "The selected keyboard is not connected.",
        EndReason.GameNotRunning => "The selected game is not running. Start the game, then press Start.",
        EndReason.DriverMissing => "The ViGEmBus driver is not installed.",
        EndReason.DriverNotStarted => "The ViGEmBus driver is installed but not running. Restart the PC to finish installing it.",
        EndReason.PadFailed => "The virtual controller could not be connected.",
        _ => "The session could not start.",
    });
}

/// <summary>What to map: the keyboard, the game, and a profile already compiled against that keyboard's calibration.</summary>
/// <param name="Keyboard">Container id of the selected keyboard.</param>
/// <param name="GamePath">Full path of the game's executable.</param>
/// <param name="Signatures">Group signatures recorded at calibration, by group; null or missing groups skip the check.</param>
public sealed record SessionRequest(
    Guid Keyboard,
    string GamePath,
    CompiledProfile Profile,
    IReadOnlyDictionary<int, GroupSignature>? Signatures = null);

/// <summary>A reading for the status card, taken on any thread.</summary>
/// <param name="GameElevated">The game has focus but runs elevated (or cannot be read) while the mapper does not, so its input is invisible.</param>
/// <param name="FallbackKeys">Analog keys driven from the keyboard's on/off state right now.</param>
/// <param name="SensorProblem">Why, while <paramref name="FallbackKeys"/> is above zero.</param>
/// <param name="KeysAwaitingRelease">The game has focus and a mapped key stays dead until it is released once.</param>
/// <param name="SubmitCount">Reports the driver accepted this session, for the submit rate.</param>
/// <param name="HookReinstalls">Times Windows removed the hook and the session put it back.</param>
public sealed record SessionStatus(
    SessionState State,
    SessionEnd? LastEnd,
    bool GameHasFocus,
    bool GameElevated,
    int FallbackKeys,
    string? SensorProblem,
    bool KeysAwaitingRelease,
    long SubmitCount,
    int HookReinstalls);

/// <summary>
/// The app-lifetime services a session uses, and the factories for its parts. The
/// defaults are the real ones; tests replace what they cannot drive.
/// </summary>
public sealed class SessionServices
{
    /// <summary>Connected boards; the session pauses when the selected one disappears.</summary>
    public required KeyboardDiscovery Keyboards { get; init; }

    /// <summary>Raw Input's keyboard event count, for hook-loss detection.</summary>
    public required Func<long> RawInputEvents { get; init; }

    public required IPowerEvents Power { get; init; }

    public Func<DriverState> DriverState { get; init; } = DriverStatus.Query;

    public Func<CancellationToken, VirtualPad> ConnectPad { get; init; } = VirtualPad.Connect;

    public Func<string, IGameProcess?> FindGame { get; init; } = GameProcess.Find;

    public Func<ForegroundFlag, IForegroundSource> CreateForeground { get; init; } = flag => new ForegroundTracker(flag);

    public Func<Guid, SensorSnapshot, PollerConfig, SensorPoller> CreatePoller { get; init; } = SensorPoller.ForKeyboard;

    /// <summary>Test mode: the hook treats injected keys as physical ones.</summary>
    public bool SwallowInjected { get; init; }
}
