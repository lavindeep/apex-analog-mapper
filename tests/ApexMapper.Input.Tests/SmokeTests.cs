namespace ApexMapper.Input.Tests;

[Trait("os", "windows")]
public class SmokeTests
{
    [Fact]
    public void project_compiles_and_runs() => true.Should().BeTrue();
}
