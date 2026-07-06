using FluentAssertions;
using Xunit;

namespace ApexMapper.Supervisor.Tests;

public class SupervisorArgsTests
{
    [Fact]
    public void Parses_the_session_id()
    {
        var ok = SupervisorArgs.TryParse(new[] { "--session", "abc123" }, out var parsed, out var error);

        ok.Should().BeTrue();
        parsed!.SessionId.Should().Be("abc123");
        error.Should().BeNull();
    }

    [Fact]
    public void Missing_session_flag_fails_with_an_error()
    {
        var ok = SupervisorArgs.TryParse(Array.Empty<string>(), out var parsed, out var error);

        ok.Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        error.Should().Contain("--session");
    }

    [Fact]
    public void Session_flag_without_a_value_fails()
    {
        var ok = SupervisorArgs.TryParse(new[] { "--session" }, out var parsed, out var error);

        ok.Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_session_value_fails(string value)
    {
        var ok = SupervisorArgs.TryParse(new[] { "--session", value }, out var parsed, out var error);

        ok.Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void An_unknown_argument_fails()
    {
        var ok = SupervisorArgs.TryParse(new[] { "--session", "abc", "--bogus" }, out var parsed, out var error);

        ok.Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().Contain("--bogus");
    }
}
