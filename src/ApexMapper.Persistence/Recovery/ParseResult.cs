namespace ApexMapper.Persistence.Recovery;

/// <summary>Classification of a single parse attempt against a versioned document.</summary>
internal enum ParseStatus
{
    /// <summary>Parsed cleanly (possibly after forward migration) at the current schema version.</summary>
    Ok,

    /// <summary>Unreadable or malformed content.</summary>
    Corrupt,

    /// <summary>A schema version newer than this build understands.</summary>
    NewerSchema,

    /// <summary>An older schema version with no registered migration path to the current one.</summary>
    UnmigratableSchema,
}

internal readonly record struct ParseResult<T>(ParseStatus Status, T? Value);
