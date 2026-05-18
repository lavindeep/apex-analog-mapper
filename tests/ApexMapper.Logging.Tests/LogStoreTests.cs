using ApexMapper.Logging;
using FluentAssertions;

namespace ApexMapper.Logging.Tests;

public class LogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-log-" + Guid.NewGuid().ToString("N"));

    public LogStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Writes_lines_to_active_file()
    {
        using var log = new LogStore(_dir, "app.log", maxBytes: 1_000_000, maxFiles: 5);
        log.Write(LogLevel.Info, "hello");
        log.Flush();
        using var stream = new FileStream(Path.Combine(_dir, "app.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        content.Should().Contain("hello").And.Contain("INFO");
    }

    [Fact]
    public void Rotates_when_active_file_exceeds_max_bytes()
    {
        using var log = new LogStore(_dir, "app.log", maxBytes: 128, maxFiles: 3);
        for (var i = 0; i < 50; i++) log.Write(LogLevel.Info, "abcdefghij " + i);
        log.Flush();

        File.Exists(Path.Combine(_dir, "app.log")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "app.log.1")).Should().BeTrue();
    }

    [Fact]
    public void Keeps_at_most_max_files()
    {
        using (var log = new LogStore(_dir, "app.log", maxBytes: 64, maxFiles: 2))
        {
            for (var i = 0; i < 200; i++) log.Write(LogLevel.Info, "line " + i);
            log.Flush();
        }
        var files = Directory.GetFiles(_dir, "app.log*");
        files.Length.Should().BeLessThanOrEqualTo(2);
    }
}
