namespace ApexMapper.App.Diagnostics;

/// <summary>
/// A single log line surfaced in the diagnostics log tail view.
/// </summary>
public sealed record LogTailEntry(DateTime TimestampUtc, string Level, string Message);
