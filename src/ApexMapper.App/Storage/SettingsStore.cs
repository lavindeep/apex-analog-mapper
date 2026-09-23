using System.IO;
using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>What the app remembers between runs. A missing member reads as its default.</summary>
/// <param name="Keyboard">Container id of the selected keyboard.</param>
/// <param name="GamePath">Executable path of the remembered game.</param>
/// <param name="ActiveProfile">Id of the profile in use.</param>
/// <param name="Prereleases">Offer prerelease updates.</param>
/// <param name="ConsentedKeyboards">Unverified keyboards whose sensors the user agreed to read, by container id.</param>
/// <param name="CheckForUpdates">Look for a newer version when the app starts.</param>
/// <param name="UpdateCheckedAt">When the last update check got an answer from GitHub.</param>
/// <param name="UpdateFound">The newer version that check found, or null when there was none.</param>
public sealed record AppSettings(
    Guid? Keyboard = null,
    string? GamePath = null,
    string? ActiveProfile = null,
    bool Prereleases = false,
    IReadOnlyList<Guid>? ConsentedKeyboards = null,
    bool CheckForUpdates = true,
    DateTimeOffset? UpdateCheckedAt = null,
    string? UpdateFound = null);

/// <summary>
/// <c>settings.json</c>. A file of unreadable text loads as the defaults with a problem
/// to show, and the next save replaces it. A file that could not be opened, or that a
/// newer version wrote, also loads as the defaults, but saving is refused until a later
/// load reads it: the defaults are not the user's settings. Called from the UI thread only.
/// </summary>
public sealed class SettingsStore(string path)
{
    public const int CurrentVersion = 1;

    private LoadResult<AppSettings>? _unsafeToSave;

    /// <summary>The last <see cref="Load"/> found a file it could not read, or one a newer version wrote, and returned defaults in its place.</summary>
    public bool LastLoadUsedDefaults => _unsafeToSave is not null;

    public (AppSettings Settings, string? Problem) Load()
    {
        var result = JsonFile.Load(path, Parse);
        _unsafeToSave = result.Status is LoadStatus.Unavailable or LoadStatus.Newer ? result : null;
        return (result.Value ?? new AppSettings(), LoadProblem.Describe(result, "settings"));
    }

    /// <summary>
    /// Reads the file, applies the change, saves the result and returns it. Reading first
    /// means a file that could not be opened at startup (an antivirus or sync client held
    /// it) is read again before it is written, and the change lands on what the file
    /// holds rather than on defaults. Throws <see cref="IOException"/> when the file
    /// cannot be read now, a newer version wrote it, or it cannot be written.
    /// </summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Load().Settings);
        Save(next);
        return next;
    }

    /// <summary>Throws <see cref="IOException"/> when the file cannot be written, cannot be read now, or could not be read at the last load.</summary>
    public void Save(AppSettings settings)
    {
        if (_unsafeToSave is { } blocked)
        {
            LoadProblem.ThrowIfUnsafeToSave(blocked, "settings");
        }
        LoadProblem.ThrowIfUnsafeToSave(JsonFile.Load(path, Parse), "settings");
        JsonFile.Save(path, JsonDocuments.Serialize(CurrentVersion, settings));
    }

    private static Parsed<AppSettings> Parse(string text) => JsonDocuments.Parse<AppSettings>(text, CurrentVersion);
}
