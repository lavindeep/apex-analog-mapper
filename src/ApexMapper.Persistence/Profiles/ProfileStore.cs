using System.Text.Json;
using System.Text.Json.Serialization;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Atomic;
using ApexMapper.Persistence.Json;

namespace ApexMapper.Persistence.Profiles;

public sealed class ProfileStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private readonly ProfileStoreOptions _options;

    public ProfileStore(ProfileStoreOptions options) => _options = options;

    public IReadOnlyList<Profile> LoadAll()
    {
        System.IO.Directory.CreateDirectory(_options.Directory);
        var result = new List<Profile>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_options.Directory, "*.json"))
        {
            if (TryLoad(file, out var p)) result.Add(p);
        }
        return result;
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(_options.Directory);
        var path = Path.Combine(_options.Directory, profile.Id + ".json");
        if (File.Exists(path)) RotateBackups(path);

        var doc = new VersionedDocument<Profile>(CurrentSchemaVersion, profile);
        var json = JsonSerializer.Serialize(doc, Options);
        AtomicFile.WriteAllText(path, json);
    }

    private void RotateBackups(string path)
    {
        for (var i = _options.BackupCount; i >= 2; i--)
        {
            var src = path + ".bak." + (i - 1);
            var dst = path + ".bak." + i;
            if (File.Exists(src))
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
        }
        var firstBackup = path + ".bak.1";
        if (File.Exists(firstBackup)) File.Delete(firstBackup);
        File.Copy(path, firstBackup);

        var overflow = path + ".bak." + (_options.BackupCount + 1);
        if (File.Exists(overflow)) File.Delete(overflow);
    }

    private static bool TryLoad(string path, out Profile profile)
    {
        profile = null!;
        try
        {
            var text = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<VersionedDocument<Profile>>(text, Options);
            if (doc is null || doc.Version != CurrentSchemaVersion || doc.Payload is null) return false;
            profile = doc.Payload;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerialization.Options);
        o.Converters.Add(new LinearOnlyCurveConverter());
        return o;
    }

    private sealed class LinearOnlyCurveConverter : JsonConverter<ICurve>
    {
        public override ICurve? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return LinearCurve.Instance;
            throw new JsonException("Curve deserialization is limited to null (linear) in Phase 1.");
        }

        public override void Write(Utf8JsonWriter writer, ICurve value, JsonSerializerOptions options)
        {
            if (value is LinearCurve) { writer.WriteNullValue(); return; }
            throw new JsonException("Curve serialization is limited to LinearCurve in Phase 1.");
        }
    }
}
