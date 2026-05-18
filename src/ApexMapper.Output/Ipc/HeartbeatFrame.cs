using MessagePack;

namespace ApexMapper.Output.Ipc;

[MessagePackObject]
public sealed record HeartbeatFrame : IFrame
{
    [Key(0)] public byte SchemaVersion { get; init; } = 1;
    [Key(1)] public long TimestampTicks { get; init; }
    [Key(2)] public long SequenceNumber { get; init; }
}
