namespace ApexMapper.Input.Abstractions.Hosting;

public interface ILogSink
{
    void Info(string message);
    void Warn(string message);
}
