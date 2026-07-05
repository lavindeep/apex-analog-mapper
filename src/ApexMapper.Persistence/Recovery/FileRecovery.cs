using ApexMapper.Persistence.Atomic;

namespace ApexMapper.Persistence.Recovery;

/// <summary>
/// Shared corrupt-file recovery for the versioned stores. On a corrupt primary it quarantines
/// the primary as evidence (never deleting it), then walks the rolling backups in order; the
/// first that parses is restored as the new primary. A newer-schema primary is left untouched.
/// </summary>
internal static class FileRecovery
{
    internal static (bool Loaded, T? Value, RecoveryReport? Report) Load<T>(
        string path, int backupCount, Func<string, ParseResult<T>> parse)
    {
        var primary = ReadAndParse(path, parse);
        switch (primary.Status)
        {
            case ParseStatus.Ok:
                return (true, primary.Value, null);
            case ParseStatus.NewerSchema:
                // Leave a newer-schema file untouched; report it so callers don't silently drop it.
                return (false, default, new RecoveryReport(path, RecoveryOutcome.NewerSchema));
        }

        // Primary is corrupt: preserve it as evidence before attempting recovery.
        Quarantine(path);

        for (var i = 1; i <= backupCount; i++)
        {
            var backup = path + ".bak." + i;
            if (!File.Exists(backup)) continue;
            var candidate = ReadAndParse(backup, parse);
            if (candidate.Status != ParseStatus.Ok) continue;

            // Restore the good backup as the new primary.
            try { AtomicFile.WriteAllBytes(path, File.ReadAllBytes(backup)); }
            catch { /* best effort: the profile still loaded from the backup */ }
            return (true, candidate.Value, new RecoveryReport(backup, RecoveryOutcome.RecoveredFromBackup));
        }

        return (false, default, new RecoveryReport(path, RecoveryOutcome.Quarantined));
    }

    private static ParseResult<T> ReadAndParse<T>(string path, Func<string, ParseResult<T>> parse)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { return new ParseResult<T>(ParseStatus.Corrupt, default); }
        return parse(text);
    }

    private static void Quarantine(string path)
    {
        var quarantine = path + ".corrupt";
        try
        {
            // Overwrite an older quarantine, but never delete the current corrupt primary silently.
            if (File.Exists(quarantine)) File.Delete(quarantine);
            if (File.Exists(path)) File.Move(path, quarantine);
        }
        catch { /* if we cannot quarantine, leave the primary in place rather than lose evidence */ }
    }
}
