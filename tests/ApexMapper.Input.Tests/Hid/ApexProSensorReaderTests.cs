using System.IO;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Hid;
using ApexMapper.Persistence.Devices;
using Xunit;

namespace ApexMapper.Input.Tests.Hid;

public sealed class ApexProSensorReaderTests
{
    private static readonly DeviceIdentity Keyboard = new(0x1038, 0x1614, null, null, null);

    // Native group 2 capture, 2026-09-13. Filtered W occupies slot 2 and reads 878 counts.
    private static byte[] CapturedGroup() => Convert.FromHexString(
        "0062035A0370034A03490355034D034A03550351034D0351036403630361035A036E034E034B0357034C034A03580351034C035103600361030000000000000000");

    [Fact]
    public void DecodesCapturedFilteredSamplesAndSendsOnlyVerifiedRequests()
    {
        using var stream = new ReplyStream(Firmware(), CapturedGroup());
        var reader = new ApexProSensorReader(Keyboard, stream);
        reader.VerifyFirmware();
        var samples = new ushort[14];
        reader.ReadFilteredGroup(2, samples);

        Assert.Equal(new ushort[] { 865, 858, 878, 846, 843, 855, 844, 842, 856, 849, 844, 849, 864, 865 }, samples);
        var firmwareRequest = new byte[65];
        firmwareRequest[1] = 0x90;
        var sensorRequest = new byte[65];
        sensorRequest[1] = 0xD7;
        sensorRequest[2] = 2;
        Assert.Equal(firmwareRequest, stream.Requests[0]);
        Assert.Equal(sensorRequest, stream.Requests[1]);
    }

    [Fact]
    public void RejectsUnsupportedDeviceAndFirmwareBeforeSensorQueries()
    {
        using var stream = new ReplyStream(Firmware("4.16.8"));
        Assert.Throws<NotSupportedException>(() => new ApexProSensorReader(Keyboard with { ProductId = 0x161C }, stream));
        var reader = new ApexProSensorReader(Keyboard, stream);
        Assert.Throws<InvalidOperationException>(() => reader.ReadFilteredGroup(1, new ushort[14]));
        Assert.Empty(stream.Requests);
        Assert.Throws<NotSupportedException>(reader.VerifyFirmware);
        Assert.Throws<InvalidOperationException>(() => reader.ReadFilteredGroup(1, new ushort[14]));
        Assert.Single(stream.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void RejectsInvalidSelectorWithoutSending(int group)
    {
        using var stream = new ReplyStream(Firmware());
        var reader = new ApexProSensorReader(Keyboard, stream);
        reader.VerifyFirmware();
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadFilteredGroup(group, new ushort[14]));
        Assert.Single(stream.Requests);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("report-id")]
    [InlineData("padding")]
    [InlineData("timeout")]
    public void InvalidSensorReplyLeavesDestinationUntouchedAndRevokesVerification(string failure)
    {
        var reply = CapturedGroup();
        if (failure == "short") reply = reply[..64];
        if (failure == "report-id") reply[0] = 1;
        if (failure == "padding") reply[64] = 1;
        if (failure == "timeout") reply = [];
        using var stream = new ReplyStream(Firmware(), reply);
        var reader = new ApexProSensorReader(Keyboard, stream);
        reader.VerifyFirmware();
        var samples = Enumerable.Repeat((ushort)999, 14).ToArray();
        Assert.Throws<IOException>(() => reader.ReadFilteredGroup(1, samples));
        Assert.All(samples, sample => Assert.Equal((ushort)999, sample));
        Assert.Throws<InvalidOperationException>(() => reader.ReadFilteredGroup(1, samples));
        Assert.Equal(2, stream.Requests.Count);
    }

    [Fact]
    public void FailedFirmwareRecheckRevokesPreviousVerification()
    {
        using var stream = new ReplyStream(Firmware(), new byte[64]);
        var reader = new ApexProSensorReader(Keyboard, stream);
        reader.VerifyFirmware();
        Assert.Throws<IOException>(reader.VerifyFirmware);
        Assert.Throws<InvalidOperationException>(() => reader.ReadFilteredGroup(1, new ushort[14]));
        Assert.Equal(2, stream.Requests.Count);
    }

    private static byte[] Firmware(string version = "4.9.1")
    {
        var response = new byte[65];
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(response, 1);
        return response;
    }

    private sealed class ReplyStream(params byte[][] replies) : IHidStream
    {
        private readonly Queue<byte[]> _replies = new(replies);
        public List<byte[]> Requests { get; } = [];
        public void Write(ReadOnlySpan<byte> buffer) => Requests.Add(buffer.ToArray());
        public int Read(Span<byte> buffer)
        {
            var reply = _replies.Dequeue();
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public void GetFeature(Span<byte> buffer) => throw new NotSupportedException();
        public void SetFeature(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
