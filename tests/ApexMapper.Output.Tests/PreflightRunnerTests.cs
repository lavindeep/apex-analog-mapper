using ApexMapper.Output.Preflight;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class PreflightRunnerTests
{
    private sealed class StubCheck : IPreflightCheck
    {
        private readonly PreflightIssue? _issue;
        public StubCheck(string id, PreflightIssue? issue)
        {
            CheckId = id;
            _issue = issue;
        }

        public string CheckId { get; }
        public PreflightIssue? Run() => _issue;
    }

    private sealed class ThrowingCheck : IPreflightCheck
    {
        public string CheckId => "boom-check";
        public PreflightIssue? Run() => throw new InvalidOperationException("kaboom");
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTime(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void All_pass_yields_no_issues_and_no_blocker()
    {
        var runner = new PreflightRunner(new IPreflightCheck[]
        {
            new StubCheck("a", null),
            new StubCheck("b", null),
        });

        var report = runner.RunAll();

        report.Issues.Should().BeEmpty();
        report.HasBlocker.Should().BeFalse();
    }

    [Fact]
    public void Warn_and_fail_are_aggregated_with_blocker()
    {
        var runner = new PreflightRunner(new IPreflightCheck[]
        {
            new StubCheck("warn", new PreflightIssue("warn", PreflightSeverity.Warn, "heads up", null)),
            new StubCheck("fail", new PreflightIssue("fail", PreflightSeverity.Fail, "no good", "fix it")),
        });

        var report = runner.RunAll();

        report.Issues.Should().HaveCount(2);
        report.HasBlocker.Should().BeTrue();
    }

    [Fact]
    public void A_throwing_check_becomes_a_fail_issue_with_its_id_and_message()
    {
        var runner = new PreflightRunner(new IPreflightCheck[] { new ThrowingCheck() });

        var report = runner.RunAll();

        var issue = report.Issues.Should().ContainSingle().Subject;
        issue.CheckId.Should().Be("boom-check");
        issue.Severity.Should().Be(PreflightSeverity.Fail);
        issue.Message.Should().Contain("kaboom");
        report.HasBlocker.Should().BeTrue();
    }

    [Fact]
    public void Issue_order_follows_check_order()
    {
        var runner = new PreflightRunner(new IPreflightCheck[]
        {
            new StubCheck("first", new PreflightIssue("first", PreflightSeverity.Warn, "1", null)),
            new StubCheck("second", new PreflightIssue("second", PreflightSeverity.Fail, "2", null)),
            new StubCheck("third", new PreflightIssue("third", PreflightSeverity.Warn, "3", null)),
        });

        var report = runner.RunAll();

        report.Issues.Select(i => i.CheckId).Should().ContainInOrder("first", "second", "third");
    }

    [Fact]
    public void Report_timestamp_comes_from_the_time_provider()
    {
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var runner = new PreflightRunner(Array.Empty<IPreflightCheck>());

        var report = runner.RunAll(new FixedTime(when));

        report.At.Should().Be(when);
    }
}
