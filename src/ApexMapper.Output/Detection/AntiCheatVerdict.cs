namespace ApexMapper.Output.Detection;

public enum AntiCheatAction { Allow, DisableAutoEnable }

public record AntiCheatVerdict(AntiCheatAction Action, string? Reason, string? MatchedSignal);
