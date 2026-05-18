namespace ApexMapper.Core.Engine;

public enum ProfilePrecedence
{
    Generic = 0,
    WindowTitle = 1,
    ExactExecutableOrAppId = 2,
    ManualPin = 3,
}

public readonly record struct ForegroundContext(
    string? ExecutableName,
    string? WindowTitle,
    string? SteamAppId);
