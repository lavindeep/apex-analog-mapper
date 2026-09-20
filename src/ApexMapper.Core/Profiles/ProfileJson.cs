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
        var profile = JsonDocuments.Deserialize<Profile>(text, CurrentVersion, out error);
        if (profile is null)
        {
            return null;
        }
        if (profile.Validate() is { } invalid)
        {
            error = invalid;
            return null;
        }
        return profile;
    }
}
