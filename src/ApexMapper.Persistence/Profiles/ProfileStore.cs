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
        var primaries = new List<string>(System.IO.Directory.GetFiles(_options.Directory, "*.json"));
        // Also pick up profiles whose primary vanished but left recovery artifacts behind
        // (a crash between quarantine and restore, or accidental deletion of the primary):
        // their backups can still be walked and the primary restored.
        var known = new HashSet<string>(primaries);
        foreach (var suffix in new[] { ".bak.1", ".corrupt" })
        {
            foreach (var artifact in System.IO.Directory.GetFiles(_options.Directory, "*.json" + suffix))
            {
                var primary = artifact[..^suffix.Length];
                if (!File.Exists(primary) && known.Add(primary)) primaries.Add(primary);
            }
        }

        foreach (var file in primaries)
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

        var existing = File.Exists(path) ? ReadStatus(path) : (ParseStatus?)null;

        // Never downgrade a file written by a newer schema version by rotating and clobbering it.
        if (existing == ParseStatus.NewerSchema)
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite '{path}': it was written by a newer schema version than {CurrentSchemaVersion}.");
        }

        var doc = new VersionedDocument<Profile>(CurrentSchemaVersion, profile);
        var json = JsonSerializer.Serialize(doc, Options);

        // Stage the new content durably first, so a failed write never consumes a backup
        // generation. Only once the temp is written do we rotate the current primary into a
        // backup and swap the new content in.
        var tmp = AtomicFile.WriteTemp(path, json);
        try
        {
            if (existing == ParseStatus.Corrupt)
            {
                // A corrupt primary holds no good generation to preserve: quarantine it as
                // evidence instead of rotating its bytes into the backup chain.
                FileRecovery.Quarantine(path);
            }
            else if (File.Exists(path))
            {
                BackupRotation.Rotate(path, _options.BackupCount);
            }
            AtomicFile.Commit(tmp, path);
        }
        catch
        {
            AtomicFile.DiscardTemp(tmp);
            throw;
        }
    }

    private static ParseStatus ReadStatus(string path)
    {
        try { return Parse(File.ReadAllText(path)).Status; }
        catch { return ParseStatus.Corrupt; }
    }

    internal static ParseResult<Profile> Parse(string text)
    {
        // Classify from the version header alone before touching the payload: a newer
        // document's payload may be shaped in a way this build cannot deserialize, and must
        // not be misread as corrupt (which would quarantine it, or downgrade it on save).
        if (!VersionedDocumentHeader.TryReadVersion(text, out var version) || version <= 0)
            return new ParseResult<Profile>(ParseStatus.Corrupt, null);
        if (version > CurrentSchemaVersion)
            return new ParseResult<Profile>(ParseStatus.NewerSchema, null);
        if (version == CurrentSchemaVersion)
            return DeserializeCurrent(text);

        // 0 < version < current: run the forward-only migration pipeline, then re-parse.
        var migrated = ProfileMigrator.Migrate(text, version, CurrentSchemaVersion);
        if (migrated is null) return new ParseResult<Profile>(ParseStatus.Corrupt, null);
        return DeserializeCurrent(migrated);
    }

    private static ParseResult<Profile> DeserializeCurrent(string text)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<VersionedDocument<Profile>>(text, Options);
            return doc?.Payload is null || doc.Version != CurrentSchemaVersion
                ? new ParseResult<Profile>(ParseStatus.Corrupt, null)
                : new ParseResult<Profile>(ParseStatus.Ok, doc.Payload);
        }
        catch
        {
            return new ParseResult<Profile>(ParseStatus.Corrupt, null);
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
