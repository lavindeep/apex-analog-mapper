namespace ApexMapper.Persistence.Profiles;

/// <summary>
/// Forward-only migration pipeline for versioned profile documents. Each step transforms a
/// document's raw JSON from one schema version to the next. There are no historical versions to
/// migrate yet, so the production step set is empty; the plumbing is exercised via the internal
/// <see cref="Migrate(string, int, int, IReadOnlyDictionary{int, Func{string, string}})"/>
/// overload with injected steps.
/// </summary>
public static class ProfileMigrator
{
    private static readonly IReadOnlyDictionary<int, Func<string, string>> Steps
        = new Dictionary<int, Func<string, string>>();

    public static bool CanMigrate(int version) => version >= 1 && version <= ProfileStore.CurrentSchemaVersion;

    /// <summary>
    /// Advances <paramref name="json"/> from <paramref name="fromVersion"/> up to
    /// <paramref name="toVersion"/> by applying each registered forward step in order. Returns the
    /// migrated JSON, or <c>null</c> if the range is invalid or a required step is missing.
    /// </summary>
    public static string? Migrate(string json, int fromVersion, int toVersion)
        => Migrate(json, fromVersion, toVersion, Steps);

    internal static string? Migrate(
        string json, int fromVersion, int toVersion, IReadOnlyDictionary<int, Func<string, string>> steps)
    {
        if (fromVersion <= 0 || fromVersion > toVersion) return null;
        var current = json;
        for (var v = fromVersion; v < toVersion; v++)
        {
            if (!steps.TryGetValue(v, out var step)) return null;
            current = step(current);
        }
        return current;
    }
}
