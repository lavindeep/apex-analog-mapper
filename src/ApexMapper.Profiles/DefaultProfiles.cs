using System.Text.Json;
using System.Text.Json.Serialization;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Json;

namespace ApexMapper.Profiles;

public static class DefaultProfiles
{
    private static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    public static Profile LoadRacing()
    {
        using var s = typeof(DefaultProfiles).Assembly.GetManifestResourceStream("ApexMapper.Profiles.Defaults.racing.json")
            ?? throw new InvalidOperationException("Embedded resource racing.json not found.");
        using var reader = new StreamReader(s);
        var json = reader.ReadToEnd();

        var doc = JsonSerializer.Deserialize<VersionedDocument<Profile>>(json, ReadOptions)
            ?? throw new InvalidOperationException("Failed to parse embedded racing profile.");
        return doc.Payload;
    }

    private static JsonSerializerOptions CreateReadOptions()
    {
        var o = new JsonSerializerOptions(JsonSerialization.Options);
        o.Converters.Add(new NullToLinearCurveConverter());
        return o;
    }

    private sealed class NullToLinearCurveConverter : JsonConverter<ICurve>
    {
        public override ICurve? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return LinearCurve.Instance;
            throw new JsonException("Curve deserialization is limited to null (linear) for default profiles.");
        }

        public override void Write(Utf8JsonWriter writer, ICurve value, JsonSerializerOptions options)
        {
            if (value is LinearCurve) { writer.WriteNullValue(); return; }
            throw new JsonException("Curve serialization is limited to LinearCurve for default profiles.");
        }
    }
}
