using ApexMapper.Logging;

namespace ApexMapper.Input.Abstractions.Hosting;

public sealed class LogStoreSink : ILogSink
{
    private readonly LogStore _store;

    public LogStoreSink(LogStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void Info(string message) => _store.Write(LogLevel.Info, message);

    public void Warn(string message) => _store.Write(LogLevel.Warn, message);
}
