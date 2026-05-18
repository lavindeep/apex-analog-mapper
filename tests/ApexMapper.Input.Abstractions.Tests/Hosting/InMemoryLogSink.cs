using ApexMapper.Input.Abstractions.Hosting;

namespace ApexMapper.Input.Abstractions.Tests.Hosting;

internal sealed class InMemoryLogSink : ILogSink
{
    public List<string> Lines { get; } = new();
    public void Info(string m) => Lines.Add("INFO " + m);
    public void Warn(string m) => Lines.Add("WARN " + m);
}
