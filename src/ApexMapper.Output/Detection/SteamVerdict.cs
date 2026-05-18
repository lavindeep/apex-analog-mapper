namespace ApexMapper.Output.Detection;

public record SteamVerdict(bool IsSteamLaunched, string? SteamAppId, string? Reason);
