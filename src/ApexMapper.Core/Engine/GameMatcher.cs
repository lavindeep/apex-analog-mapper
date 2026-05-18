namespace ApexMapper.Core.Engine;

public sealed record GameMatcher(
    string? ExecutableName,
    string? WindowTitlePattern,
    string? SteamAppId);
