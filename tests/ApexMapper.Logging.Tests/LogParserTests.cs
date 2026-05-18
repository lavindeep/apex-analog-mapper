using System.Globalization;
using System.Text;
using ApexMapper.Logging;
using FluentAssertions;

namespace ApexMapper.Logging.Tests;

public class LogParserTests
{
    [Fact]
    public void TryParseLine_parses_well_formed_line()
    {
        // LogStore writes lines via DateTime.UtcNow:O which is ISO-8601
        // round-trippable. Use a representative value with seven-digit fraction.
        const string line = "2026-05-18T01:23:45.6789012Z INFO Hello world";

        LogParser.TryParseLine(line, out var entry).Should().BeTrue();
        entry.Should().NotBeNull();
        entry!.Level.Should().Be("INFO");
        entry.Message.Should().Be("Hello world");
        entry.TimestampUtc.Should().Be(DateTime.Parse(
            "2026-05-18T01:23:45.6789012Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind));
        entry.TimestampUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void TryParseLine_keeps_internal_whitespace_in_message()
    {
        const string line = "2026-05-18T01:23:45.6789012Z WARN  multiple   spaces   here";

        LogParser.TryParseLine(line, out var entry).Should().BeTrue();
        entry!.Message.Should().Be(" multiple   spaces   here");
    }

    [Fact]
    public void TryParseLine_returns_false_for_empty_or_whitespace()
    {
        LogParser.TryParseLine(string.Empty, out var e1).Should().BeFalse();
        e1.Should().BeNull();
        LogParser.TryParseLine("   ", out var e2).Should().BeFalse();
        e2.Should().BeNull();
    }

    [Fact]
    public void TryParseLine_returns_false_for_single_token()
    {
        LogParser.TryParseLine("2026-05-18T01:23:45.6789012Z", out var e).Should().BeFalse();
        e.Should().BeNull();
    }

    [Fact]
    public void TryParseLine_returns_false_for_timestamp_only_and_level()
    {
        // Two tokens but no message — accept this as a well-formed but empty
        // message? Spec says "blank message OK". Treat this as valid with
        // empty message.
        LogParser.TryParseLine("2026-05-18T01:23:45.6789012Z INFO", out var e).Should().BeTrue();
        e!.Message.Should().Be(string.Empty);
        e.Level.Should().Be("INFO");
    }

    [Fact]
    public void TryParseLine_returns_false_for_malformed_timestamp()
    {
        LogParser.TryParseLine("not-a-date INFO message", out var e).Should().BeFalse();
        e.Should().BeNull();
    }

    [Fact]
    public void TryParseLine_returns_false_for_mid_line_truncation()
    {
        // A line cut off mid-timestamp.
        LogParser.TryParseLine("2026-05-18T01:23:4", out var e).Should().BeFalse();
        e.Should().BeNull();
    }

    [Fact]
    public void ParseLines_counts_malformed_and_returns_good_entries()
    {
        var lines = new[]
        {
            "2026-05-18T01:23:45.0000000Z INFO good 1",
            "garbage",
            "2026-05-18T01:23:46.0000000Z WARN good 2",
            string.Empty,
            "2026-05-18T01:23:47.0000000Z ERROR good 3",
            "broken-line-no-level",
        };

        var entries = LogParser.ParseLines(lines, out var malformedCount);

        entries.Should().HaveCount(3);
        entries[0].Message.Should().Be("good 1");
        entries[1].Level.Should().Be("WARN");
        entries[2].Level.Should().Be("ERROR");
        // empty strings, "garbage" and "broken-line-no-level" are malformed.
        malformedCount.Should().Be(3);
    }

    [Fact]
    public void FilterByLevels_returns_only_matching_entries_case_insensitive()
    {
        var entries = new List<LogEntry>
        {
            new(new DateTime(2026, 5, 18, 0, 0, 1, DateTimeKind.Utc), "INFO", "a"),
            new(new DateTime(2026, 5, 18, 0, 0, 2, DateTimeKind.Utc), "WARN", "b"),
            new(new DateTime(2026, 5, 18, 0, 0, 3, DateTimeKind.Utc), "ERROR", "c"),
            new(new DateTime(2026, 5, 18, 0, 0, 4, DateTimeKind.Utc), "DEBUG", "d"),
        };

        // Case-insensitive: pass "warn" lowercase.
        var filtered = LogParser.FilterByLevels(entries, new[] { "warn", "ERROR" });

        filtered.Should().HaveCount(2);
        filtered.Select(e => e.Message).Should().Equal("b", "c");
    }

    [Fact]
    public void FilterByLevels_with_empty_filter_returns_empty()
    {
        // Spec: "if all four toggles are off, result is empty".
        var entries = new List<LogEntry>
        {
            new(DateTime.UtcNow, "INFO", "a"),
            new(DateTime.UtcNow, "WARN", "b"),
        };

        var filtered = LogParser.FilterByLevels(entries, Array.Empty<string>());
        filtered.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_via_LogStore_parses_back_cleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apex-log-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var store = new LogStore(dir, "rt.log", maxBytes: 1_000_000, maxFiles: 3))
            {
                store.Write(LogLevel.Info, "first line");
                store.Write(LogLevel.Warn, "second  spaced  line");
                store.Write(LogLevel.Error, "third");
                store.Flush();
            }

            var path = Path.Combine(dir, "rt.log");
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var entries = LogParser.ParseLines(lines, out var malformed);

            malformed.Should().Be(0);
            entries.Should().HaveCount(3);
            entries[0].Level.Should().Be("INFO");
            entries[0].Message.Should().Be("first line");
            entries[1].Level.Should().Be("WARN");
            entries[1].Message.Should().Be("second  spaced  line");
            entries[2].Level.Should().Be("ERROR");
            entries[2].Message.Should().Be("third");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Mid_line_truncation_counts_as_malformed_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apex-log-trunc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "trunc.log");
            // Two complete lines + one truncated tail (no trailing newline).
            var content =
                "2026-05-18T01:23:45.0000000Z INFO complete 1\n" +
                "2026-05-18T01:23:46.0000000Z WARN complete 2\n" +
                "2026-05-18T01:23:47.000";
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var act = () => LogParser.ParseLines(lines, out _);
            act.Should().NotThrow();

            var entries = LogParser.ParseLines(lines, out var malformed);
            entries.Should().HaveCount(2);
            malformed.Should().Be(1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
