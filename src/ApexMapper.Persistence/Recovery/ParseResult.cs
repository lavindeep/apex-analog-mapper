namespace ApexMapper.Persistence.Recovery;

/// <summary>Classification of a single parse attempt against a versioned document.</summary>
internal enum ParseStatus
{
    /// <summary>Parsed cleanly (possibly after forward migration) at the current schema version.</summary>
    Ok,

    /// <summary>Unreadable, malformed, or an unusable/unmigratable version.</summary>
    Corrupt,

    /// <summary>A schema version newer than this build understands.</summary>
    NewerSchema,
}

internal readonly record struct ParseResult<T>(ParseStatus Status, T? Value);
