using ApexMapper.Input.Abstractions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Composition;

/// <summary>Adapts the input host's minimal <see cref="ILogSink"/> onto the
/// app's <see cref="ILogger"/> infrastructure.</summary>
internal sealed class LoggerLogSink(ILogger logger) : ILogSink
{
    public void Info(string message) => logger.LogInformation("{Message}", message);

    public void Warn(string message) => logger.LogWarning("{Message}", message);
}
