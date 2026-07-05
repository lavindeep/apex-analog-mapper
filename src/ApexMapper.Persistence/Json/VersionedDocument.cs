using System.Text.Json;

namespace ApexMapper.Persistence.Json;

public sealed record VersionedDocument<T>(int Version, T Payload);

/// <summary>
/// Reads only the schema version header of a versioned document. Classification must never
/// depend on the payload shape: a newer document's payload may not deserialize under this
/// build's types, and a full-document parse would misreport such a file as corrupt.
/// </summary>
internal static class VersionedDocumentHeader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// True when <paramref name="text"/> is a JSON object carrying an integral
    /// <c>version</c> property; the payload is never inspected.
    /// </summary>
    internal static bool TryReadVersion(string text, out int version)
    {
        version = 0;
        try
        {
            using var doc = JsonDocument.Parse(text, DocumentOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out version);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
