using System.Text.Json;
using System.Text.Json.Serialization;
using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Storage;

/// <summary>
/// The one JSON dialect every file uses: snake_case names, enums as snake_case
/// strings, scan codes as hex strings, indented, comments and trailing commas
/// tolerated, unknown members ignored. Every document is wrapped in an envelope with a
/// version integer so a newer file is refused rather than misread.
/// </summary>
public static class JsonDocuments
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new ScanCodeConverter() },
    };

    private sealed record Envelope<T>(int Version, T Payload);

    public static string Serialize<T>(int version, T payload) =>
        JsonSerializer.Serialize(new Envelope<T>(version, payload), Options);

    /// <summary>Returns null and sets the error for a malformed or newer document.</summary>
    public static T? Deserialize<T>(string text, int currentVersion, out string? error) where T : class
    {
        var parsed = Parse<T>(text, currentVersion);
        error = parsed.Error;
        return parsed.Value;
    }

    /// <summary>The payload, or why the document cannot be used. A newer format is refused and marked, never misread.</summary>
    public static Parsed<T> Parse<T>(string text, int currentVersion) where T : class
    {
        int version;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out version))
            {
                return new(null, "The file has no version number.");
            }
        }
        catch (JsonException e)
        {
            return new(null, "The file is not valid JSON: " + e.Message);
        }
        if (version > currentVersion)
        {
            return new(null, $"The file was written by a newer version of the app (format {version}, this app reads up to {currentVersion}).", Newer: true);
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(text, Options);
            return envelope?.Payload is { } payload ? new(payload, null) : new(null, "The file has no content.");
        }
        catch (JsonException e)
        {
            return new(null, "The file could not be read: " + e.Message);
        }
    }

    private sealed class ScanCodeConverter : JsonConverter<ScanCode>
    {
        public override ScanCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException("Scan code must be a hex string like 0x11.");
            if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !ushort.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var value)
                || !ScanCode.IsValid(value))
            {
                throw new JsonException($"'{text}' is not a valid scan code.");
            }
            return new ScanCode(value);
        }

        public override void Write(Utf8JsonWriter writer, ScanCode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());

        public override ScanCode ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Read(ref reader, typeToConvert, options);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, ScanCode value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.ToString());
    }
}
