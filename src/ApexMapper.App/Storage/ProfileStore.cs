using System.IO;
using System.Text.RegularExpressions;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>A profile file as listed. <see cref="Profile"/> is null when the file cannot be used.</summary>
/// <param name="Problem">What the window should tell the user about this file, or null.</param>
public sealed record ProfileEntry(string Id, Profile? Profile, string? Problem);

/// <summary>
/// Profiles, one JSON file each in the profiles folder, named by id. The Forza profile
/// always exists: it is written when missing or unreadable and cannot be deleted. Reset
/// gives any profile the Forza bindings under its own id and name. Called from the UI
/// thread only.
/// </summary>
public sealed partial class ProfileStore(string directory)
{
    public IReadOnlyList<ProfileEntry> List()
    {
        var entries = new List<ProfileEntry> { Load(DefaultProfiles.ForzaId)! };
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (Path.GetExtension(path) == ".json" && IsValidId(id) && id != DefaultProfiles.ForzaId && Load(id) is { } entry)
            {
                entries.Add(entry);
            }
        }
        return
        [
            entries[0],
            .. entries.Skip(1).OrderBy(e => e.Profile?.Name ?? e.Id, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>The profile with this id, or null when there is no such file.</summary>
    public ProfileEntry? Load(string id)
    {
        RequireValidId(id);
        var result = JsonFile.Load(PathOf(id), Parse);
        if (id == DefaultProfiles.ForzaId && result.Value is null && result.Status != LoadStatus.Unavailable)
        {
            var forza = DefaultProfiles.Forza();
            JsonFile.Save(PathOf(id), ProfileJson.Serialize(forza));
            var problem = result.Status == LoadStatus.Corrupt
                ? $"The Forza profile could not be read and was reset to the default. {result.Error}"
                : null;
            return new ProfileEntry(id, forza, problem);
        }
        if (result.Status == LoadStatus.NotFound)
        {
            return null;
        }
        // The file name is the id; a hand-edited id inside the file does not move it.
        var profile = result.Value is { } value && value.Id != id ? value with { Id = id } : result.Value;
        return new ProfileEntry(id, profile, LoadProblem.Describe(result, $"profile \"{id}\""));
    }

    /// <summary>
    /// Writes the profile unless the file already holds the same content. Returns whether
    /// it changed, which is what decides that an edit stops a running session.
    /// </summary>
    public bool Save(Profile profile)
    {
        RequireValidId(profile.Id);
        if (profile.Validate() is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(profile));
        }
        var text = ProfileJson.Serialize(profile);
        var path = PathOf(profile.Id);
        if (JsonFile.Load(path, Parse).Value is { } current && ProfileJson.Serialize(current with { Id = profile.Id }) == text)
        {
            return false;
        }
        JsonFile.Save(path, text);
        return true;
    }

    /// <summary>Deletes the profile and its backup. False for the Forza profile, which cannot be deleted.</summary>
    public bool Delete(string id)
    {
        RequireValidId(id);
        if (id == DefaultProfiles.ForzaId)
        {
            return false;
        }
        // The backup goes too, or the next load would restore the profile from it.
        File.Delete(PathOf(id));
        File.Delete(JsonFile.BackupPath(PathOf(id)));
        return true;
    }

    /// <summary>Replaces the profile's bindings with Forza's, keeping its id and name.</summary>
    public Profile Reset(string id)
    {
        var forza = DefaultProfiles.Forza();
        var name = id == DefaultProfiles.ForzaId ? forza.Name : Load(id)?.Profile?.Name ?? id;
        var reset = forza with { Id = id, Name = name };
        Save(reset);
        return reset;
    }

    /// <summary>A plain file name: lowercase letters, digits, dashes and underscores, at most 64 characters.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    private string PathOf(string id) => Path.Combine(directory, id + ".json");

    private static (Profile? Value, string? Error) Parse(string text) => (ProfileJson.Deserialize(text, out var error), error);

    private static void RequireValidId(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"'{id}' is not a valid profile id.", nameof(id));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdPattern();
}
