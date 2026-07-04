using MessagePack;

namespace ApexMapper.Output.Ipc;

[Union(0, typeof(ControlFrame))]
[Union(1, typeof(HeartbeatFrame))]
[Union(2, typeof(ZeroFrame))]
[Union(3, typeof(PanicFrame))]
public interface IFrame
{
    /// <summary>Current IPC schema version stamped by senders. A frame whose
    /// <see cref="SchemaVersion"/> is 0 arrived from a peer that predates
    /// versioning (or omitted the field) and must be treated as unknown.</summary>
    public const byte CurrentSchemaVersion = 1;

    byte SchemaVersion { get; }
}
