namespace ApexMapper.Output;

public enum OutputStatus { Disconnected, Connecting, Connected, Faulted }

public record OutputStatusChanged(OutputStatus Status, string? Error, DateTimeOffset At);
