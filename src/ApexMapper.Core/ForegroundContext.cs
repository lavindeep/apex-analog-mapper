namespace ApexMapper.Core;

public sealed record ForegroundContext(
    string ExecutablePath,
    string WindowTitle,
    uint ProcessId,
    string? SteamAppId,
    System.DateTimeOffset ObservedAt)
{
    public static ForegroundContext Empty { get; } =
        new(string.Empty, string.Empty, 0u, null, System.DateTimeOffset.MinValue);
}
