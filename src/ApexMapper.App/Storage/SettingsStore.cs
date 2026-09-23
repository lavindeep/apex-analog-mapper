using System.IO;
using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>What the app remembers between runs. A missing member reads as its default.</summary>
/// <param name="Keyboard">Container id of the selected keyboard.</param>
/// <param name="GamePath">Executable path of the remembered game.</param>
/// <param name="ActiveProfile">Id of the profile in use.</param>
/// <param name="Prereleases">Offer prerelease updates.</param>
public sealed record AppSettings(
    Guid? Keyboard = null,
    string? GamePath = null,
    string? ActiveProfile = null,
    bool Prereleases = false);

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

    public (AppSettings Settings, string? Problem) Load()
    {
        var result = JsonFile.Load(path, text => JsonDocuments.Parse<AppSettings>(text, CurrentVersion));
        _unsafeToSave = result.Status is LoadStatus.Unavailable or LoadStatus.Newer ? result : null;
        return (result.Value ?? new AppSettings(), LoadProblem.Describe(result, "settings"));
    }

    /// <summary>Throws <see cref="IOException"/> when the file cannot be written, or after a load that could not read it.</summary>
    public void Save(AppSettings settings)
    {
        if (_unsafeToSave is { } blocked)
        {
            LoadProblem.ThrowIfUnsafeToSave(blocked, "settings");
        }
        JsonFile.Save(path, JsonDocuments.Serialize(CurrentVersion, settings));
    }
}
