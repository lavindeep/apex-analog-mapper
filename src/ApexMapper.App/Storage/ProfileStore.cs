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
/// always exists: when its file gives nothing the default stands in, and the default is
/// written back when the file is missing or holds unreadable text. It cannot be deleted.
/// Saving refuses to write over a file that could not be read or that a newer version
/// of the app wrote. Called from the UI thread only.
/// </summary>
public sealed partial class ProfileStore(string directory)
{
    private static readonly HashSet<string> ReservedNames =
    [
        "con", "prn", "aux", "nul",
        .. Enumerable.Range(0, 10).Select(i => $"com{i}"),
        .. Enumerable.Range(0, 10).Select(i => $"lpt{i}"),
    ];

    public IReadOnlyList<ProfileEntry> List()
    {
        // Forza first: on a first run, loading it writes it, and with it the folder.
        var forza = Load(DefaultProfiles.ForzaId)!;
        var others = new List<ProfileEntry>();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                // Windows file names ignore case, so Drift.JSON is the file Load("drift") reads.
                var id = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                    && id != DefaultProfiles.ForzaId
                    && IsValidId(id)
                    && Load(id) is { } entry)
                {
                    others.Add(entry);
                }
            }
        }
        return [forza, .. others.OrderBy(e => e.Profile?.Name ?? e.Id, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The profile with this id, or null when there is no such file. Never null for the Forza profile.</summary>
    public ProfileEntry? Load(string id)
    {
        RequireValidId(id);
        var result = JsonFile.Load(PathOf(id), ProfileJson.Parse);
        if (id == DefaultProfiles.ForzaId && result.Value is null)
        {
            return new ProfileEntry(id, DefaultProfiles.Forza(), StandInForForza(result));
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
    /// it changed. Throws <see cref="IOException"/> rather than write over a file that
    /// could not be read or that a newer version wrote.
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
        var current = JsonFile.Load(path, ProfileJson.Parse);
        LoadProblem.ThrowIfUnsafeToSave(current, $"profile \"{profile.Id}\"");
        if (current.Value is { } existing && ProfileJson.Serialize(existing with { Id = profile.Id }) == text)
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
        // The backup goes first: left behind, the next load would restore the profile from it.
        File.Delete(JsonFile.BackupPath(PathOf(id)));
        File.Delete(PathOf(id));
        return true;
    }

    /// <summary>
    /// Replaces the profile's bindings with Forza's. A custom profile keeps its id and
    /// name; the Forza profile gets its default name back. Returns whether the file changed.
    /// </summary>
    public (Profile Profile, bool Changed) Reset(string id)
    {
        var forza = DefaultProfiles.Forza();
        var name = id == DefaultProfiles.ForzaId ? forza.Name : Load(id)?.Profile?.Name ?? id;
        var reset = forza with { Id = id, Name = name };
        return (reset, Save(reset));
    }

    /// <summary>
    /// A plain file name: lowercase letters, digits, dashes and underscores, at most 64
    /// characters, and not a name Windows reserves for a device.
    /// </summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id) && !ReservedNames.Contains(id);

    /// <summary>Why the default stands in for Forza's file, writing it back when the file is missing or unreadable text.</summary>
    private string? StandInForForza(LoadResult<Profile> result)
    {
        if (result.Status is LoadStatus.Unavailable or LoadStatus.Newer)
        {
            return $"{LoadProblem.Describe(result, "Forza profile")} The default Forza profile is used meanwhile.";
        }
        try
        {
            JsonFile.Save(PathOf(DefaultProfiles.ForzaId), ProfileJson.Serialize(DefaultProfiles.Forza()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The Forza profile could not be saved, so the default is used without being saved: {e.Message}";
        }
        return result.Status == LoadStatus.Corrupt ? $"The Forza profile could not be read and was reset to the default. {result.Error}" : null;
    }

    private string PathOf(string id) => Path.Combine(directory, id + ".json");

    private static void RequireValidId(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"'{id}' is not a valid profile id.", nameof(id));
        }
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,63}\z")]
    private static partial Regex IdPattern();
}
