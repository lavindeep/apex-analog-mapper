using ApexMapper.App.Diagnostics;
using ApexMapper.App.Diagnostics.LogTail;
using FluentAssertions;

namespace ApexMapper.App.Tests.Diagnostics.LogTail;

public class LogTailFilterTests
{
    private static IReadOnlyList<LogTailEntry> Sample() => new List<LogTailEntry>
    {
        new(new DateTime(2026, 5, 18, 0, 0, 1, DateTimeKind.Utc), "DEBUG", "d"),
        new(new DateTime(2026, 5, 18, 0, 0, 2, DateTimeKind.Utc), "INFO",  "i"),
        new(new DateTime(2026, 5, 18, 0, 0, 3, DateTimeKind.Utc), "WARN",  "w"),
        new(new DateTime(2026, 5, 18, 0, 0, 4, DateTimeKind.Utc), "ERROR", "e"),
    };

    [Fact]
    public void Single_level_filter_returns_only_that_level()
    {
        var result = LogTailFilter.Apply(Sample(), new[] { "WARN" });
        result.Should().HaveCount(1);
        result[0].Message.Should().Be("w");
    }

    [Fact]
    public void Multi_level_filter_returns_union()
    {
        var result = LogTailFilter.Apply(Sample(), new[] { "INFO", "WARN" });
        result.Select(e => e.Message).Should().BeEquivalentTo(new[] { "i", "w" });
    }

    [Fact]
    public void Empty_filter_set_returns_empty_list()
    {
        var result = LogTailFilter.Apply(Sample(), Array.Empty<string>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Filter_matches_case_insensitively()
    {
        var result = LogTailFilter.Apply(Sample(), new[] { "warn", "Error" });
        result.Select(e => e.Message).Should().BeEquivalentTo(new[] { "w", "e" });
    }
}
