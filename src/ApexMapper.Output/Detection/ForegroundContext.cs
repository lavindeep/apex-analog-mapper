namespace ApexMapper.Output.Detection;

public sealed record ForegroundContext(
    int ProcessId,
    string? ExecutablePath,
    string? WindowTitle,
    string? SteamAppId,
    DateTimeOffset CapturedAt);
