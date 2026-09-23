using ApexMapper.Core.Storage;

namespace ApexMapper.Core.Profiles;

/// <summary>Profile files: a version 1 envelope around <see cref="Profile"/>.</summary>
public static class ProfileJson
{
    public const int CurrentVersion = 1;

    public static string Serialize(Profile profile) => JsonDocuments.Serialize(CurrentVersion, profile);

    /// <summary>Returns null and sets the error for malformed, newer, or invalid profiles.</summary>
    public static Profile? Deserialize(string text, out string? error)
    {
        var parsed = Parse(text);
        error = parsed.Error;
        return parsed.Value;
    }

    /// <summary>The profile, or why it cannot be used, with a newer format marked.</summary>
    public static Parsed<Profile> Parse(string text)
    {
        var parsed = JsonDocuments.Parse<Profile>(text, CurrentVersion);
        return parsed.Value?.Validate() is { } invalid ? new(null, invalid) : parsed;
    }
}
