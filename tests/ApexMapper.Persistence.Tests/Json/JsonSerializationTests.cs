using ApexMapper.Persistence.Json;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Json;

public class JsonSerializationTests
{
    private sealed record Sample(string FirstName, int AgeYears);

    [Fact]
    public void Serializes_with_snake_case_property_names()
    {
        var json = JsonSerialization.Serialize(new Sample("Lavin", 30));
        json.Should().Contain("\"first_name\"").And.Contain("\"age_years\"");
    }

    [Fact]
    public void Round_trips_a_record()
    {
        var sample = new Sample("Lavin", 30);
        var json = JsonSerialization.Serialize(sample);
        var back = JsonSerialization.Deserialize<Sample>(json)!;
        back.Should().BeEquivalentTo(sample);
    }

    [Fact]
    public void Versioned_document_round_trips()
    {
        var doc = new VersionedDocument<Sample>(Version: 1, Payload: new Sample("L", 30));
        var json = JsonSerialization.Serialize(doc);
        json.Should().Contain("\"version\": 1");
        JsonSerialization.Deserialize<VersionedDocument<Sample>>(json)!.Version.Should().Be(1);
    }
}
