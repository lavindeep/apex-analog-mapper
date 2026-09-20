using System.Buffers.Binary;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class GroupSignatureTests
{
    private static ushort[] RawOf(string fixture)
    {
        var bytes = Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".hex")).Trim());
        var raw = new ushort[14];
        var filtered = new ushort[14];
        Assert.Null(SensorProtocol.ParseGroup(bytes, raw, filtered));
        return raw;
    }

    [Fact]
    public void Rest_fixtures_give_distinct_signatures_for_the_groups_the_profile_uses()
    {
        var group2 = GroupSignature.FromRest(RawOf("rest-group2"));
        var group3 = GroupSignature.FromRest(RawOf("rest-group3"));
        Assert.Equal(0, group2.AbsentMask);
        Assert.True(group3.IsAbsent(12));
        Assert.NotEqual(group2, group3);
    }

    [Fact]
    public void A_shifted_reply_fails_and_held_keys_do_not()
    {
        var group2 = GroupSignature.FromRest(RawOf("rest-group2"));
        var group3 = GroupSignature.FromRest(RawOf("rest-group3"));
        Assert.True(group2.Matches(RawOf("rest-group2")));
        Assert.True(group2.Matches(RawOf("w-held-group2")));
        Assert.False(group2.Matches(RawOf("rest-group3")));
        Assert.False(group3.Matches(RawOf("rest-group2")));
        Assert.True(group3.Matches(RawOf("w-held-group3")));
    }

    [Fact]
    public void A_firmware_reply_in_a_sensor_slot_fails_every_signature()
    {
        var firmware = Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "firmware.hex")).Trim());
        var raw = new ushort[14];
        for (var i = 0; i < 14; i++)
        {
            raw[i] = BinaryPrimitives.ReadUInt16LittleEndian(firmware.AsSpan(1 + 2 * i, 2));
        }
        for (var group = 1; group <= 5; group++)
        {
            Assert.False(GroupSignature.FromRest(RawOf($"rest-group{group}")).Matches(raw));
        }
    }
}
