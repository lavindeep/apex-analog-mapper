using System.IO;
using System.Text;
using ApexMapper.App.Diagnostics;
using ApexMapper.App.Diagnostics.LogTail;
using ApexMapper.Logging;
using FluentAssertions;

namespace ApexMapper.App.Tests.Diagnostics.LogTail;

public class LogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-logtail-" + Guid.NewGuid().ToString("N"));

    public LogTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ActivePath => Path.Combine(_dir, "app.log");

    private void WriteLines(string path, int count, string levelPrefix, DateTime startUtc)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var ts = startUtc.AddMilliseconds(i).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            sb.Append(ts).Append(' ').Append(levelPrefix).Append(' ').Append("line ").Append(i).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    [Fact]
    public void Load_returns_all_entries_when_file_has_fewer_than_max()
    {
        WriteLines(ActivePath, 50, "INFO", DateTime.UtcNow);
        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);

        var result = tail.Load(200);

        result.Should().HaveCount(50);
        tail.MalformedCount.Should().Be(0);
    }

    [Fact]
    public void Load_returns_last_N_entries_when_file_is_oversize()
    {
        WriteLines(ActivePath, 500, "INFO", DateTime.UtcNow);
        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);

        var result = tail.Load(200);

        result.Should().HaveCount(200);
        // The last entry's message should be "line 499" (the newest).
        result[^1].Message.Should().Be("line 499");
        result[0].Message.Should().Be("line 300");
    }

    [Fact]
    public void Load_walks_rotated_files_when_active_is_short()
    {
        // Older rotated file has 200 lines, active has 50. Loading 200 must
        // pull from both files and keep the 200 most-recent in chronological
        // order.
        var start = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        WriteLines(ActivePath + ".1", 200, "INFO", start);
        WriteLines(ActivePath, 50, "WARN", start.AddSeconds(1));

        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);
        var result = tail.Load(200);

        result.Should().HaveCount(200);
        // The newest entry should be from the active (WARN) file.
        result[^1].Level.Should().Be("WARN");
        // The first 150 should be the tail of the rotated file (INFO).
        result.Take(150).Should().OnlyContain(e => e.Level == "INFO");
        result.Skip(150).Should().OnlyContain(e => e.Level == "WARN");
    }

    [Fact]
    public void Load_returns_empty_for_empty_file()
    {
        File.WriteAllText(ActivePath, string.Empty);
        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);

        var result = tail.Load(200);

        result.Should().BeEmpty();
        tail.MalformedCount.Should().Be(0);
    }

    [Fact]
    public void Load_returns_empty_for_nonexistent_file_without_throwing()
    {
        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => Path.Combine(_dir, "missing.log"));

        Action act = () => tail.Load(200);
        act.Should().NotThrow();
        tail.Load(200).Should().BeEmpty();
    }

    [Fact]
    public void Load_handles_mid_line_truncation_and_counts_malformed()
    {
        var content =
            "2026-05-18T01:00:00.0000000Z INFO good 1\n" +
            "2026-05-18T01:00:01.0000000Z WARN good 2\n" +
            "garbage line here\n" +
            "another garbage\n" +
            "2026-05-18T01:00:02.0000000Z ERROR good 3\n" +
            "2026-05-18T01:00:0";
        File.WriteAllText(ActivePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);
        var result = tail.Load(200);

        result.Should().HaveCount(3);
        tail.MalformedCount.Should().Be(3);
    }

    [Fact]
    public void Load_uses_FileShare_so_LogStore_writer_does_not_block_it()
    {
        // Open the LogStore as a writer (it holds the active file open) and
        // ensure LogTail can still read it.
        using var store = new LogStore(_dir, "app.log", maxBytes: 1_000_000, maxFiles: 3);
        store.Write(LogLevel.Info, "while-open");
        store.Flush();

        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);
        var result = tail.Load(200);

        result.Should().HaveCount(1);
        result[0].Message.Should().Be("while-open");
    }

    [Fact]
    public void Filter_delegates_to_level_filter()
    {
        var entries = new List<LogTailEntry>
        {
            new(new DateTime(2026, 5, 18, 0, 0, 1, DateTimeKind.Utc), "INFO", "a"),
            new(new DateTime(2026, 5, 18, 0, 0, 2, DateTimeKind.Utc), "WARN", "b"),
            new(new DateTime(2026, 5, 18, 0, 0, 3, DateTimeKind.Utc), "ERROR", "c"),
        };

        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);
        var filtered = tail.Filter(entries, new[] { "WARN" });

        filtered.Should().HaveCount(1);
        filtered[0].Message.Should().Be("b");
    }

    [Fact]
    public void MalformedCount_starts_at_zero_before_first_load()
    {
        var tail = new ApexMapper.App.Diagnostics.LogTail.LogTail(() => ActivePath);
        tail.MalformedCount.Should().Be(0);
    }
}
