using ApexMapper.Persistence.Atomic;
using ApexMapper.Persistence.Json;
using ApexMapper.Persistence.Recovery;

namespace ApexMapper.Persistence.Devices;

public sealed record DeviceRegistry(
    DeviceIdentity? SelectedDevice,
    IReadOnlyList<KeyCalibration> Calibrations)
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultBackupCount = 5;

    private static DeviceRegistry Empty => new(null, Array.Empty<KeyCalibration>());

    public static DeviceRegistry Load(string path) => Load(path, out _);

    /// <summary>
    /// Loads the registry, recovering a corrupt file from its rolling backups where possible.
    /// <paramref name="recovery"/> receives a report when the file was recovered, quarantined, or
    /// skipped as a newer schema; it is <c>null</c> on a clean load or a missing file.
    /// </summary>
    public static DeviceRegistry Load(string path, out RecoveryReport? recovery, int backupCount = DefaultBackupCount)
    {
        recovery = null;
        // A missing primary still recovers when rolling backups exist (e.g. a crash between
        // quarantine and restore, or accidental deletion of the file itself).
        if (!File.Exists(path) && !FileRecovery.AnyBackupExists(path, backupCount)) return Empty;
        AtomicFile.SweepStaleTemps(Path.GetDirectoryName(path)!);
        var (loaded, value, report) = FileRecovery.Load(path, backupCount, Parse);
        recovery = report;
        return loaded ? value! : Empty;
    }

    public static void Save(string path, DeviceRegistry registry, int backupCount = DefaultBackupCount)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        AtomicFile.SweepStaleTemps(dir);

        var existing = File.Exists(path) ? ReadStatus(path) : (ParseStatus?)null;

        // Never downgrade a file written by a newer schema version by rotating and clobbering it.
        if (existing == ParseStatus.NewerSchema)
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite '{path}': it was written by a newer schema version than {CurrentSchemaVersion}.");
        }

        var doc = new VersionedDocument<DeviceRegistry>(CurrentSchemaVersion, registry);
        // Stage the new content first, then rotate the current primary into a backup and swap in.
        var tmp = AtomicFile.WriteTemp(path, JsonSerialization.Serialize(doc));
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
                BackupRotation.Rotate(path, backupCount);
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

    internal static ParseResult<DeviceRegistry> Parse(string text)
    {
        // Classify from the version header alone before touching the payload: a newer
        // document's payload may be shaped in a way this build cannot deserialize, and must
        // not be misread as corrupt (which would quarantine it, or downgrade it on save).
        if (!VersionedDocumentHeader.TryReadVersion(text, out var version) || version <= 0)
            return new ParseResult<DeviceRegistry>(ParseStatus.Corrupt, null);
        if (version > CurrentSchemaVersion)
            return new ParseResult<DeviceRegistry>(ParseStatus.NewerSchema, null);
        if (version == CurrentSchemaVersion)
        {
            try
            {
                var doc = JsonSerialization.Deserialize<VersionedDocument<DeviceRegistry>>(text);
                return doc?.Payload is null
                    ? new ParseResult<DeviceRegistry>(ParseStatus.Corrupt, null)
                    : new ParseResult<DeviceRegistry>(ParseStatus.Ok, doc.Payload);
            }
            catch
            {
                return new ParseResult<DeviceRegistry>(ParseStatus.Corrupt, null);
            }
        }

        // No historical device-registry versions exist yet, so a lower version has no
        // migration path; leave the document in place rather than quarantining it.
        return new ParseResult<DeviceRegistry>(ParseStatus.UnmigratableSchema, null);
    }
}
