using ApexMapper.Core.Pipeline;
using ApexMapper.Profiles;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Profiles;

public class DefaultProfileFixtureTests
{
    [Fact]
    public void Racing_profile_parses()
    {
        var profile = DefaultProfiles.LoadRacing();
        profile.Id.Should().Be("racing");
        profile.SingleBindings.Select(b => b.Target).Should().Contain(new[]
        {
            BindingTarget.RightTrigger,
            BindingTarget.LeftTrigger,
        });
        profile.AxisBindings.Should().ContainSingle(b => b.Target == BindingTarget.LeftStickX);
    }
}
