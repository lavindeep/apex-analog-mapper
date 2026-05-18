using MessagePack;

namespace ApexMapper.Output.Ipc;

[Union(0, typeof(ControlFrame))]
[Union(1, typeof(HeartbeatFrame))]
[Union(2, typeof(ZeroFrame))]
[Union(3, typeof(PanicFrame))]
public interface IFrame
{
    byte SchemaVersion { get; }
}
