using System.Text.Json;
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
        o.Converters.Add(new SingleKeyBindingConverter());
        o.Converters.Add(new AxisPairBindingConverter());
        return o;
    }
}
