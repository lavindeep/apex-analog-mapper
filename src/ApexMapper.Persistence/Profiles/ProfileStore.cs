using System.Text.Json;
using System.Text.Json.Serialization;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Atomic;
using ApexMapper.Persistence.Json;
using ApexMapper.Persistence.Recovery;

namespace ApexMapper.Persistence.Profiles;

public sealed class ProfileStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private readonly ProfileStoreOptions _options;

    public ProfileStore(ProfileStoreOptions options) => _options = options;

    public IReadOnlyList<Profile> LoadAll() => LoadAll(out _);

    /// <summary>
    /// Loads every profile in the directory, recovering corrupt files from their rolling
    /// backups where possible. <paramref name="recoveries"/> receives one entry per file that
    /// was recovered, quarantined, or skipped as a newer schema; clean loads produce no entry.
    /// </summary>
    public IReadOnlyList<Profile> LoadAll(out IReadOnlyList<RecoveryReport> recoveries)
    {
        System.IO.Directory.CreateDirectory(_options.Directory);
        AtomicFile.SweepStaleTemps(_options.Directory);
        var result = new List<Profile>();
        var reports = new List<RecoveryReport>();
        // Materialize first: recovery renames files (quarantine/restore) mid-scan.
        foreach (var file in System.IO.Directory.GetFiles(_options.Directory, "*.json"))
        {
            var (loaded, value, report) = FileRecovery.Load(file, _options.BackupCount, Parse);
            if (report is not null) reports.Add(report);
            if (loaded) result.Add(value!);
        }
        recoveries = reports;
        return result;
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(_options.Directory);
        AtomicFile.SweepStaleTemps(_options.Directory);
        var path = Path.Combine(_options.Directory, profile.Id + ".json");

        var doc = new VersionedDocument<Profile>(CurrentSchemaVersion, profile);
        var json = JsonSerializer.Serialize(doc, Options);

        // Stage the new content durably first, so a failed write never consumes a backup
        // generation. Only once the temp is written do we rotate the current primary into a
        // backup and swap the new content in.
        var tmp = AtomicFile.WriteTemp(path, json);
        try
        {
            if (File.Exists(path)) BackupRotation.Rotate(path, _options.BackupCount);
            AtomicFile.Commit(tmp, path);
        }
        catch
        {
            AtomicFile.DiscardTemp(tmp);
            throw;
        }
    }

    internal static ParseResult<Profile> Parse(string text)
    {
        VersionedDocument<Profile>? doc;
        try
        {
            doc = JsonSerializer.Deserialize<VersionedDocument<Profile>>(text, Options);
        }
        catch
        {
            return new ParseResult<Profile>(ParseStatus.Corrupt, null);
        }

        if (doc is null || doc.Payload is null || doc.Version <= 0)
            return new ParseResult<Profile>(ParseStatus.Corrupt, null);
        if (doc.Version == CurrentSchemaVersion)
            return new ParseResult<Profile>(ParseStatus.Ok, doc.Payload);
        if (doc.Version > CurrentSchemaVersion)
            return new ParseResult<Profile>(ParseStatus.NewerSchema, null);

        // 0 < version < current: forward migration is wired in a later step; treat as unusable for now.
        return new ParseResult<Profile>(ParseStatus.Corrupt, null);
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
