using ApexMapper.Core.Bindings;
using ApexMapper.Core.Profiles;
using Xunit;

namespace ApexMapper.Core.Tests.Profiles;

public class ProfileJsonTests
{
    [Fact]
    public void Forza_round_trips()
    {
        var text = ProfileJson.Serialize(DefaultProfiles.Forza());
        var back = ProfileJson.Deserialize(text, out var error);
        Assert.Null(error);
        var forza = DefaultProfiles.Forza();
        Assert.Equal(forza.Id, back!.Id);
        Assert.Equal(forza.Name, back.Name);
        Assert.Equal(forza.Keys, back.Keys);
        Assert.Equal(forza.Axes, back.Axes);
    }

    [Fact]
    public void Json_uses_snake_case_hex_keys_and_string_enums()
    {
        var text = ProfileJson.Serialize(DefaultProfiles.Forza());
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"key\": \"0x11\"", text);
        Assert.Contains("\"target\": \"right_trigger\"", text);
        Assert.Contains("\"press_ramp_ms\"", text);
        Assert.Contains("\"conflict\": \"last_input_wins\"", text);
        Assert.Contains("\"negative_key\": \"0xE04B\"", text);
    }

    [Fact]
    public void Unknown_members_are_ignored_and_comments_tolerated()
    {
        var text = ProfileJson.Serialize(DefaultProfiles.Forza())
            .Replace("\"name\": \"Forza\"", "// a comment\n\"name\": \"Forza\", \"future_field\": 42");
        var back = ProfileJson.Deserialize(text, out var error);
        Assert.Null(error);
        Assert.Equal("Forza", back!.Name);
    }

    [Fact]
    public void Newer_version_is_refused_with_a_clear_message()
    {
        var text = ProfileJson.Serialize(DefaultProfiles.Forza()).Replace("\"version\": 1", "\"version\": 2");
        Assert.Null(ProfileJson.Deserialize(text, out var error));
        Assert.Contains("newer version", error);
    }

    [Fact]
    public void Invalid_content_is_refused()
    {
        Assert.Null(ProfileJson.Deserialize("not json", out var error));
        Assert.NotNull(error);

        var reserved = DefaultProfiles.Forza() with
        {
            Keys = [new(new Core.Keys.ScanCode(0x1D), PadTarget.ButtonA, Core.Response.Response.Linear, 0f, 0f)],
        };
        var text = Core.Storage.JsonDocuments.Serialize(ProfileJson.CurrentVersion, reserved);
        Assert.Null(ProfileJson.Deserialize(text, out error));
        Assert.Contains("reserved", error);

        var badKey = ProfileJson.Serialize(DefaultProfiles.Forza()).Replace("\"0x11\"", "\"0x1234\"");
        Assert.Null(ProfileJson.Deserialize(badKey, out error));
        Assert.NotNull(error);
    }
}
