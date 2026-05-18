using ApexMapper.Persistence.Profiles;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Profiles;

public class ProfileMigratorTests
{
    [Fact]
    public void CanMigrate_returns_true_for_current_version()
    {
        ProfileMigrator.CanMigrate(ProfileStore.CurrentSchemaVersion).Should().BeTrue();
    }

    [Fact]
    public void CanMigrate_returns_false_for_unknown_future_version()
    {
        ProfileMigrator.CanMigrate(ProfileStore.CurrentSchemaVersion + 1).Should().BeFalse();
    }

    [Fact]
    public void CanMigrate_returns_false_for_zero_or_negative()
    {
        ProfileMigrator.CanMigrate(0).Should().BeFalse();
        ProfileMigrator.CanMigrate(-1).Should().BeFalse();
    }
}
