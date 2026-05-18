namespace ApexMapper.Input.Abstractions.Backends;

public sealed record BackendStatusChanged(BackendKind Kind, BackendStatus Status, string? Reason);
