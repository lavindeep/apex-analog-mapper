namespace ApexMapper.Persistence.Recovery;

/// <summary>What happened to a persisted file that could not be loaded normally.</summary>
public enum RecoveryOutcome
{
    /// <summary>The primary file was corrupt; a good rolling backup was loaded and restored as the new primary.</summary>
    RecoveredFromBackup,

    /// <summary>The primary and every backup were unreadable; the corrupt primary was preserved as evidence and defaults were used.</summary>
    Quarantined,

    /// <summary>The file was written by a newer schema version than this build understands; it was left untouched.</summary>
    NewerSchema,
}

/// <summary>A per-file report of a non-normal load outcome. Clean loads produce no report.</summary>
public sealed record RecoveryReport(string File, RecoveryOutcome Outcome);
