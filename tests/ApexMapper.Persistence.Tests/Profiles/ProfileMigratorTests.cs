using ApexMapper.Persistence.Profiles;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Profiles;

public class ProfileMigratorTests
{
    [Fact]
    public void Migrate_applies_registered_forward_steps_in_order()
    {
        var steps = new Dictionary<int, Func<string, string>>
        {
            [1] = json => json + "+1to2",
            [2] = json => json + "+2to3",
        };

        var result = ProfileMigrator.Migrate("doc", fromVersion: 1, toVersion: 3, steps);

        result.Should().Be("doc+1to2+2to3");
    }

    [Fact]
    public void Migrate_returns_null_when_a_step_is_missing()
    {
        var steps = new Dictionary<int, Func<string, string>>(); // no v1->v2 step

        // Null signals an unmigratable document: the stores classify it as
        // UnmigratableSchema and leave the file in place instead of quarantining it.
        ProfileMigrator.Migrate("doc", fromVersion: 1, toVersion: 2, steps).Should().BeNull();
    }

    [Fact]
    public void Migrate_is_a_noop_when_already_at_the_target_version()
    {
        var steps = new Dictionary<int, Func<string, string>>();

        ProfileMigrator.Migrate("doc", fromVersion: 1, toVersion: 1, steps).Should().Be("doc");
    }

    [Fact]
    public void Migrate_returns_null_for_an_invalid_range()
    {
        var steps = new Dictionary<int, Func<string, string>>();

        ProfileMigrator.Migrate("doc", fromVersion: 0, toVersion: 1, steps).Should().BeNull();
        ProfileMigrator.Migrate("doc", fromVersion: 3, toVersion: 2, steps).Should().BeNull();
    }
}
