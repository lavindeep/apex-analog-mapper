using ApexMapper.Core.Bindings;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Storage;
using Xunit;

namespace ApexMapper.Core.Tests.Profiles;

public class ProfileJsonTests
{
    private static Profile Custom() => new("custom", "Custom",
        [new(DefaultProfiles.Key.W, PadTarget.RightTrigger, Core.Response.Response.Create(2f, 0.85f, 0.15f), 25f, 40f)],
        [new(DefaultProfiles.Key.A, DefaultProfiles.Key.D, PadTarget.LeftStickX, Core.Response.Response.Aggressive, 10f, 20f, ConflictRule.Neutral, AxisMode.Rate, 123f, 77f)]);

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
    public void Non_default_members_round_trip()
    {
        var text = ProfileJson.Serialize(Custom());
        Assert.Contains("\"mode\": \"rate\"", text);
        Assert.Contains("\"conflict\": \"neutral\"", text);
        var back = ProfileJson.Deserialize(text, out var error);
        Assert.Null(error);
        Assert.Equal(Custom().Keys, back!.Keys);
        Assert.Equal(Custom().Axes, back.Axes);
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
    public void Newer_version_is_refused_and_older_is_read()
    {
        var text = ProfileJson.Serialize(DefaultProfiles.Forza()).Replace("\"version\": 1", "\"version\": 2");
        Assert.Null(ProfileJson.Deserialize(text, out var error));
        Assert.Contains("newer version", error);

        var older = ProfileJson.Serialize(DefaultProfiles.Forza()).Replace("\"version\": 1", "\"version\": 0");
        Assert.NotNull(ProfileJson.Deserialize(older, out error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("42")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("{\"payload\": {}}")]
    [InlineData("{\"version\": \"1\", \"payload\": {}}")]
    [InlineData("{\"version\": 1}")]
    [InlineData("{\"version\": 1, \"payload\": null}")]
    [InlineData("{\"version\": 1, \"payload\": {\"id\": \"x\", \"name\": \"y\"}}")]
    [InlineData("{\"version\": 1, \"payload\": {\"id\": \"x\", \"name\": \"y\", \"keys\": [null], \"axes\": []}}")]
    [InlineData("{\"version\": 1, \"payload\": {\"id\": \"x\", \"name\": \"y\", \"keys\": [{\"key\": \"0x39\", \"target\": \"button_a\"}], \"axes\": []}}")]
    [InlineData("{\"version\": 1, \"payload\": {\"id\": \"x\", \"name\": \"y\", \"keys\": [{\"key\": \"0x11\", \"target\": \"right_trigger\", \"response\": {\"exponent\": 0, \"saturation\": 0, \"deadzone\": 0}}], \"axes\": []}}")]
    [InlineData("{\"version\": 1, \"payload\": {\"id\": \"x\", \"name\": \"y\", \"keys\": [{\"key\": 17, \"target\": \"button_a\", \"response\": {\"exponent\": 1, \"saturation\": 1, \"deadzone\": 0}}], \"axes\": []}}")]
    public void Malformed_documents_are_refused_with_a_message_and_never_throw(string text)
    {
        Assert.Null(ProfileJson.Deserialize(text, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Invalid_content_is_refused()
    {
        var reserved = DefaultProfiles.Forza() with
        {
            Keys = [new(new ScanCode(0x1D), PadTarget.ButtonA, Core.Response.Response.Linear, 0f, 0f)],
        };
        var text = JsonDocuments.Serialize(ProfileJson.CurrentVersion, reserved);
        Assert.Null(ProfileJson.Deserialize(text, out var error));
        Assert.Contains("reserved", error);

        var badKey = ProfileJson.Serialize(DefaultProfiles.Forza()).Replace("\"0x11\"", "\"0x1234\"");
        Assert.Null(ProfileJson.Deserialize(badKey, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_pad_target_bound_twice_is_refused()
    {
        var forza = DefaultProfiles.Forza();
        var twoKeysOneButton = forza with
        {
            Keys = [.. forza.Keys, new(new ScanCode(0x2C), PadTarget.ButtonA, Core.Response.Response.Linear, 0f, 0f)],
        };
        Assert.Contains("ButtonA", twoKeysOneButton.Validate());
        var twoAxesOneStick = forza with
        {
            Axes = [.. forza.Axes, forza.Axes[0] with { NegativeKey = new ScanCode(0x2C), PositiveKey = new ScanCode(0x2E) }],
        };
        Assert.Contains("LeftStickX", twoAxesOneStick.Validate());
    }

    [Fact]
    public void Scan_codes_work_as_dictionary_keys()
    {
        var text = JsonDocuments.Serialize(1, new Dictionary<ScanCode, int> { [DefaultProfiles.Key.W] = 7, [DefaultProfiles.Key.Left] = 8 });
        Assert.Contains("\"0x11\": 7", text);
        var back = JsonDocuments.Deserialize<Dictionary<ScanCode, int>>(text, 1, out var error);
        Assert.Null(error);
        Assert.Equal(7, back![DefaultProfiles.Key.W]);
        Assert.Equal(8, back[DefaultProfiles.Key.Left]);
    }
}

public class BindingValidationTests
{
    private static readonly ScanCode W = DefaultProfiles.Key.W;
    private static readonly ScanCode A = DefaultProfiles.Key.A;
    private static readonly Core.Response.Response Linear = Core.Response.Response.Linear;

    [Fact]
    public void Key_binding_rules()
    {
        Assert.Null(KeyBinding.Validate(W, PadTarget.RightTrigger, Linear, 0f, 0f));
        Assert.Contains("two keys", KeyBinding.Validate(W, PadTarget.LeftStickX, Linear, 0f, 0f));
        Assert.Contains("response", KeyBinding.Validate(W, PadTarget.ButtonA, null, 0f, 0f));
        Assert.Contains("Exponent", KeyBinding.Validate(W, PadTarget.ButtonA, new Core.Response.Response(9f, 1f, 0f), 0f, 0f));
        Assert.Contains("Ramps", KeyBinding.Validate(W, PadTarget.ButtonA, Linear, -1f, 0f));
        Assert.Contains("Ramps", KeyBinding.Validate(W, PadTarget.ButtonA, Linear, 0f, float.NaN));
    }

    [Fact]
    public void Axis_binding_rules()
    {
        Assert.Null(AxisBinding.Validate(A, W, PadTarget.LeftStickX, Linear, 0f, 0f, 150f, 0f));
        Assert.Contains("different", AxisBinding.Validate(A, A, PadTarget.LeftStickX, Linear, 0f, 0f, 150f, 100f));
        Assert.Contains("not a stick axis", AxisBinding.Validate(A, W, PadTarget.ButtonA, Linear, 0f, 0f, 150f, 100f));
        Assert.Contains("Rate", AxisBinding.Validate(A, W, PadTarget.LeftStickX, Linear, 0f, 0f, 0f, 100f));
        Assert.Contains("Rate", AxisBinding.Validate(A, W, PadTarget.LeftStickX, Linear, 0f, 0f, float.PositiveInfinity, 100f));
        Assert.Contains("Rate", AxisBinding.Validate(A, W, PadTarget.LeftStickX, Linear, 0f, 0f, 150f, -1f));
        Assert.Contains("response", AxisBinding.Validate(A, W, PadTarget.LeftStickX, null, 0f, 0f, 150f, 100f));
        Assert.Contains("Ramps", AxisBinding.Validate(A, W, PadTarget.LeftStickX, Linear, float.NaN, 0f, 150f, 100f));
    }
}
