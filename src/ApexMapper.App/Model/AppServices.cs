using System.Diagnostics;
using ApexMapper.App.Storage;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.Model;

/// <summary>Key events from Raw Input, drained on the UI thread for key capture and the other-keyboard notice.</summary>
public interface IKeyEvents
{
    bool TryRead(out RawKeyEvent key);

    /// <summary>The physical keyboard a Raw Input device handle belongs to, or null when Windows cannot say.</summary>
    Guid? ContainerOf(nint device);
}

/// <summary>The app's one Raw Input pump as <see cref="IKeyEvents"/>.</summary>
public sealed class RawInputKeyEvents(RawInputPump pump) : IKeyEvents
{
    public bool TryRead(out RawKeyEvent key) => pump.TryDequeue(out key);

    public Guid? ContainerOf(nint device) => pump.ContainerIdOf(device);
}

/// <summary>Questions the view models ask the user.</summary>
public interface IDialogs
{
    /// <summary>A yes or no question; true when the user picked <paramref name="confirm"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirm);

    /// <summary>Where to save a JSON file, or null when the user cancelled.</summary>
    string? AskSavePath(string suggestedName);
}

/// <summary>
/// The outside world as the view models see it. The app fills it with the real things;
/// view model tests fill it with fakes, and never with a real session, whose hook would
/// swallow the keys of whoever uses the PC.
/// </summary>
public sealed class AppServices
{
    public required IMappingSession Session { get; init; }

    public required KeyboardDiscovery Keyboards { get; init; }

    public required ILiveSensor Sensor { get; init; }

    public required IKeyEvents KeyEvents { get; init; }

    public required ProfileStore Profiles { get; init; }

    public required CalibrationStore Calibrations { get; init; }

    public required SettingsStore Settings { get; init; }

    public required IDialogs Dialogs { get; init; }

    /// <summary>Runs an action on the UI thread later, without waiting: how events from other threads reach the view models.</summary>
    public required Action<Action> Post { get; init; }

    /// <summary>Asks a board for its firmware off the UI thread: opening the interface can take a few hundred milliseconds.</summary>
    public Func<Guid, Task<FirmwareReading>> ReadFirmware { get; init; } = board => Task.Run(() => FirmwareProbe.Read(board));

    public Func<IReadOnlyList<GameWindow>> ListWindows { get; init; } = GameWindows.List;

    public Func<DriverState> DriverState { get; init; } = DriverStatus.Query;

    public Func<ScanCode, string> KeyName { get; init; } = KeyNames.Of;

    /// <summary>Opens a web page or a folder in the shell.</summary>
    public Action<string> Open { get; init; } = _ => { };

    /// <summary>Starts a new copy of the app and closes this one.</summary>
    public Action Restart { get; init; } = () => { };

    /// <summary>Writes a line to the app log: session events and faults, never key presses.</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Milliseconds on a monotonic clock, for sampling windows and rates.</summary>
    public Func<long> NowMs { get; init; } = () => Environment.TickCount64;

    /// <summary>The clock Raw Input stamps key events with (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    public Func<long> Timestamp { get; init; } = Stopwatch.GetTimestamp;

    public string AppVersion { get; init; } = "0.0.0";

    /// <summary>The folder with the log and settings, for the setup card's button.</summary>
    public string DataFolder { get; init; } = "";
}
