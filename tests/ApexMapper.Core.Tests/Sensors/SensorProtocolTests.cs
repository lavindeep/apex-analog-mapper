using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class SensorProtocolTests
{
    private static byte[] Fixture(string name) =>
        Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".hex")).Trim());

    [Fact]
    public void Firmware_fixture_parses_to_4_9_1()
    {
        Assert.Null(SensorProtocol.ParseFirmware(Fixture("firmware"), out var version));
        Assert.Equal("4.9.1", version);
    }

    [Fact]
    public void Rest_fixtures_parse_and_show_which_slots_have_keys()
    {
        var raw = new ushort[14];
        var filtered = new ushort[14];
        for (var group = 1; group <= 5; group++)
        {
            Assert.Null(SensorProtocol.ParseGroup(Fixture($"rest-group{group}"), raw, filtered));
            Assert.True(SensorProtocol.IsPlausibleGroup(raw));
        }

        // Group 3 on the US layout: slot 12 has no key, everything else does.
        SensorProtocol.ParseGroup(Fixture("rest-group3"), raw, filtered);
        Assert.False(SensorProtocol.HasSensor(raw[12]));
        Assert.True(SensorProtocol.HasSensor(raw[1]));
        Assert.InRange(raw[1], 800, 900);
    }

    [Fact]
    public void Held_w_fixture_shows_w_at_the_ceiling()
    {
        var raw = new ushort[14];
        var filtered = new ushort[14];
        Assert.Null(SensorProtocol.ParseGroup(Fixture("w-held-group2"), raw, filtered));
        Assert.Equal(4095, raw[2]);
        Assert.InRange(filtered[2], 4090, 4095);
        Assert.InRange(raw[1], 800, 900);
    }

    [Fact]
    public void Structural_checks_reject_bad_replies()
    {
        var raw = new ushort[14];
        var filtered = new ushort[14];
        var good = Fixture("rest-group2");

        var wrongLength = good[..64];
        Assert.NotNull(SensorProtocol.ParseGroup(wrongLength, raw, filtered));

        var badId = (byte[])good.Clone();
        badId[0] = 1;
        Assert.NotNull(SensorProtocol.ParseGroup(badId, raw, filtered));

        var badPadding = (byte[])good.Clone();
        badPadding[60] = 7;
        Assert.NotNull(SensorProtocol.ParseGroup(badPadding, raw, filtered));

        var overRange = (byte[])good.Clone();
        overRange[2] = 0x10;
        Assert.NotNull(SensorProtocol.ParseGroup(overRange, raw, filtered));
    }

    [Fact]
    public void Plausibility_rejects_all_zero_and_all_identical_groups()
    {
        Assert.False(SensorProtocol.IsPlausibleGroup(new ushort[14]));
        var same = new ushort[14];
        Array.Fill(same, (ushort)850);
        Assert.False(SensorProtocol.IsPlausibleGroup(same));
    }

    [Fact]
    public void Firmware_parsing_rejects_non_version_replies()
    {
        var reply = new byte[65];
        Assert.NotNull(SensorProtocol.ParseFirmware(reply, out _));
        "abc"u8.CopyTo(reply.AsSpan(1));
        Assert.NotNull(SensorProtocol.ParseFirmware(reply, out _));
        Array.Clear(reply);
        "4.9.1"u8.CopyTo(reply.AsSpan(1));
        reply[40] = 3;
        Assert.NotNull(SensorProtocol.ParseFirmware(reply, out _));
        reply[40] = 0;
        reply[0] = 1;
        Assert.NotNull(SensorProtocol.ParseFirmware(reply, out _));
    }

    [Fact]
    public void Only_two_commands_can_be_built()
    {
        var report = new byte[65];
        SensorRequest.Firmware().WriteTo(report);
        Assert.Equal(0x90, report[1]);
        Assert.Equal(0, report[2]);
        SensorRequest.Group(3).WriteTo(report);
        Assert.Equal(0xD7, report[1]);
        Assert.Equal(3, report[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorRequest.Group(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorRequest.Group(6));

        var constructors = typeof(SensorRequest).GetConstructors();
        Assert.Empty(constructors);
    }
}
