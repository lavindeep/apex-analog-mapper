namespace ApexMapper.Logging;

/// <summary>
/// Cross-platform parsed log entry. Lives in <c>ApexMapper.Logging</c> so the
/// parser can be unit-tested on non-Windows hosts. The WPF-facing
/// <c>ApexMapper.App.Diagnostics.LogTailEntry</c> mirrors this record and is
/// produced by <c>LogTail</c> at the App boundary.
/// </summary>
public sealed record LogEntry(DateTime TimestampUtc, string Level, string Message);
