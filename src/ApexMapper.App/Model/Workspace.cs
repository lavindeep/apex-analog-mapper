using System.IO;
using ApexMapper.App.Mvvm;
using ApexMapper.App.Storage;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.Model;

/// <summary>The chosen keyboard, as the cards need it.</summary>
/// <param name="Info">What discovery found.</param>
/// <param name="Firmware">What the board answered to the firmware request.</param>
/// <param name="Consented">The user agreed to have this unverified board's sensors read.</param>
public sealed record Board(KeyboardInfo Info, FirmwareReading Firmware, bool Consented)
{
    public Guid Id => Info.ContainerId;

    /// <summary>The product has been verified with the sensor protocol.</summary>
    public bool Verified => KnownKeyboards.Find(Info.ProductId) is { Verified: true };

    /// <summary>The firmware is one the protocol was verified on. Another version only warns.</summary>
    public bool FirmwareVerified => Firmware.Version is { } version && KnownKeyboards.VerifiedFirmware.Contains(version);

    /// <summary>The app may send sensor requests: the board answered with a version, and it is verified or the user agreed.</summary>
    public bool CanReadSensors => Firmware.Version is not null && (Verified || Consented);
}

/// <summary>
/// What the cards share, on the UI thread: the chosen keyboard with its calibration,
/// the game, the active profile as saved, and whether a session is up. Each card writes
/// the part it owns and watches the rest.
/// </summary>
public sealed class Workspace : ObservableObject
{
    private Board? _board;
    private KeyboardCalibration? _calibration;
    private string? _gamePath;
    private Profile? _activeProfile;
    private SessionState _session;
    private string? _runningProfileText;
    private string? _loadProblem;
    private string? _saveProblem;
    private DriverState _driver = DriverState.Unknown;
    private bool _readingFirmware;

    /// <summary>The chosen keyboard while it is connected and its firmware was asked; null otherwise. Set by the keyboard card.</summary>
    public Board? Board
    {
        get => _board;
        set => Set(ref _board, value);
    }

    /// <summary>The chosen keyboard's calibration on its current firmware. Set by the calibration card.</summary>
    public KeyboardCalibration? Calibration
    {
        get => _calibration;
        set
        {
            if (Set(ref _calibration, value))
            {
                Raise(nameof(Map));
            }
        }
    }

    /// <summary>Which sensor each key reads: the built-in table, overridden by what calibration learned.</summary>
    public SensorMap Map => _calibration is { Keys.Count: > 0 } calibration
        ? new SensorMap(calibration.Keys.ToDictionary(k => k.Key, k => k.Value.SensorIndex))
        : SensorMap.Default;

    /// <summary>A keyboard is chosen and its firmware is being asked, so <see cref="Board"/> is null for now. Set by the keyboard card.</summary>
    public bool ReadingFirmware
    {
        get => _readingFirmware;
        set => Set(ref _readingFirmware, value);
    }

    /// <summary>The ViGEmBus driver as last checked. Set by the setup card.</summary>
    public DriverState Driver
    {
        get => _driver;
        set => Set(ref _driver, value);
    }

    /// <summary>Executable path of the chosen game. Set by the game card.</summary>
    public string? GamePath
    {
        get => _gamePath;
        set => Set(ref _gamePath, value);
    }

    /// <summary>The active profile as saved; what Start compiles. Set by the profile card.</summary>
    public Profile? ActiveProfile
    {
        get => _activeProfile;
        set => Set(ref _activeProfile, value);
    }

    /// <summary>The session's state; Starting from the moment Start is pressed. Set by the status card.</summary>
    public SessionState Session
    {
        get => _session;
        set
        {
            if (Set(ref _session, value))
            {
                Raise(nameof(SessionActive));
            }
        }
    }

    public bool SessionActive => _session != SessionState.Idle;

    /// <summary>
    /// The active profile as the running session compiled it, serialised, or null when no
    /// session runs. Saving the active profile stops the session only when the saved
    /// text differs from this (E8).
    /// </summary>
    public string? RunningProfileText
    {
        get => _runningProfileText;
        set => Set(ref _runningProfileText, value);
    }

    /// <summary>
    /// Why the settings file could not be read or written, or null. Shown on the status card.
    /// Set as the app starts to the load problem; a failed save shows instead until a save works.
    /// </summary>
    public string? SettingsProblem
    {
        get => _saveProblem ?? _loadProblem;
        init => _loadProblem = value;
    }

    /// <summary>
    /// The settings file could not be read at startup, so this run began from defaults.
    /// Until the app restarts, only choices the user makes are saved, each landing on what
    /// the file holds; a choice a card makes on its own would put a default over the
    /// user's setting. The startup problem stays on the status card meanwhile.
    /// </summary>
    public bool SettingsUnread { get; init; }

    /// <summary>
    /// Saves one change to the settings, or records why it could not be saved. The change
    /// still holds for this run. <paramref name="byUser"/> is false for a choice a card
    /// makes on its own, such as the only keyboard plugged in.
    /// </summary>
    public void Remember(SettingsStore settings, Func<AppSettings, AppSettings> change, bool byUser = true)
    {
        if (!byUser && SettingsUnread)
        {
            return;
        }
        try
        {
            settings.Update(change);
            // A save that works clears a failed one; the startup problem stays until the app restarts.
            ReportSettings(SettingsUnread ? _loadProblem : null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ReportSettings(_loadProblem, "Settings could not be saved: " + e.Message);
        }
    }

    private void ReportSettings(string? loadProblem, string? saveProblem)
    {
        var before = SettingsProblem;
        (_loadProblem, _saveProblem) = (loadProblem, saveProblem);
        if (SettingsProblem != before)
        {
            Raise(nameof(SettingsProblem));
        }
    }
}
