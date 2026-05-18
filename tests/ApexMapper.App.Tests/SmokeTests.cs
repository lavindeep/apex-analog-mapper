using FluentAssertions;

namespace ApexMapper.App.Tests;

public class SmokeTests
{
    [Fact]
    public void Project_compiles()
    {
        true.Should().BeTrue();
    }
}
