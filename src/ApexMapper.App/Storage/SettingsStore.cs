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
/// <c>settings.json</c>. An unreadable file loads as the defaults with a problem to
/// show, and the next save replaces it. Called from the UI thread only.
/// </summary>
public sealed class SettingsStore(string path)
{
    public const int CurrentVersion = 1;

    public (AppSettings Settings, string? Problem) Load()
    {
        var result = JsonFile.Load(path, text => (JsonDocuments.Deserialize<AppSettings>(text, CurrentVersion, out var error), error));
        return (result.Value ?? new AppSettings(), LoadProblem.Describe(result, "settings"));
    }

    public void Save(AppSettings settings) => JsonFile.Save(path, JsonDocuments.Serialize(CurrentVersion, settings));
}
