using ApexMapper.Core.Pipeline;

namespace ApexMapper.Output;

public interface IControllerOutput
{
    bool IsConnected { get; }
    string? LastError { get; }
    void Connect();
    void Submit(in VirtualPadState state);
    void Zero();
    void Disconnect();
}
