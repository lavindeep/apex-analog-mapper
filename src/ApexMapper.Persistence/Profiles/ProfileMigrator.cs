using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexMapper.Persistence.Profiles;

/// <summary>
/// Forward-only migration pipeline for versioned profile documents. Each step transforms a
/// document's raw JSON from one schema version to the next. The v1 -> v2 step only bumps the
/// version header: v2 added the optional per-binding <c>inner_deadzone</c>, <c>outer_deadzone</c>
/// and <c>curve</c> fields, all of which default when absent, so a v1 payload is already a valid
/// v2 payload.
/// </summary>
internal static class ProfileMigrator
{
    private static readonly IReadOnlyDictionary<int, Func<string, string>> Steps
        = new Dictionary<int, Func<string, string>>
        {
            [1] = BumpVersionTo2,
        };

    // Accept exactly what the rest of the pipeline accepts: the store's serializer and the
    // version-header reader both tolerate comments and trailing commas, so the migrator must too.
    private static readonly JsonDocumentOptions LenientOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // v1 payloads are structurally valid v2 payloads; only the version header advances.
    private static string BumpVersionTo2(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: LenientOptions)
            ?? throw new InvalidOperationException("Cannot migrate a null profile document.");
        node["version"] = 2;
        return node.ToJsonString();
    }

    /// <summary>
    /// Advances <paramref name="json"/> from <paramref name="fromVersion"/> up to
    /// <paramref name="toVersion"/> by applying each registered forward step in order. Returns the
    /// migrated JSON, or <c>null</c> if the range is invalid or a required step is missing —
    /// the stores classify a <c>null</c> as an unmigratable document and leave the file in place.
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
