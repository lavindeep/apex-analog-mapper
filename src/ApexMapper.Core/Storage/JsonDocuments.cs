using System.Text.Json;
using System.Text.Json.Serialization;
using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Storage;

/// <summary>
/// The one JSON dialect every file uses: snake_case names, enums as snake_case names
/// and never numbers, scan codes as hex strings, indented, comments and trailing commas
/// tolerated, unknown members ignored. Every document is wrapped in an envelope with a
/// version integer so a newer file is refused rather than misread.
///
/// Once a version has shipped, any change its reader would refuse or silently drop (a
/// new member, a new enum value, a looser limit) raises the version, and the raised
/// version is written under a new file name, so an older app keeps reading its own file
/// and never meets a newer one. A stricter check keeps the version: the reader repairs
/// or drops what the check now forbids, and never refuses a file an older app wrote.
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
        Converters = { new EnumNameConverter(), new ScanCodeConverter() },
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

    /// <summary>
    /// An enum by its snake_case name, in any case, and nothing else. The stock converter
    /// also takes a number or a comma list, either of which can read as a value no name has.
    /// </summary>
    private sealed class EnumNameConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ByName<>).MakeGenericType(typeToConvert))!;

        private sealed class ByName<T> : JsonConverter<T> where T : struct, Enum
        {
            private static readonly Dictionary<T, string> Names = Enum.GetValues<T>()
                .ToDictionary(value => value, value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));

            private static readonly Dictionary<string, T> Values = Names
                .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value))
                {
                    return value;
                }
                throw new JsonException($"Expected one of: {string.Join(", ", Names.Values)}.");
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                writer.WriteStringValue(Names[value]);
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
