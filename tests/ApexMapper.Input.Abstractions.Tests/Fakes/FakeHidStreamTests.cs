using ApexMapper.Input.Abstractions.Tests.Fakes;

namespace ApexMapper.Input.Abstractions.Tests.Fakes;

public class FakeHidStreamTests
{
    [Fact]
    public void Read_returns_canned_reports_in_order_then_zero()
    {
        var stream = new FakeHidStream(new[]
        {
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[] { 0xAA, 0xBB },
        });
        Span<byte> buf = stackalloc byte[8];

        var n1 = stream.Read(buf);
        n1.Should().Be(3);
        buf[..n1].ToArray().Should().Equal(0x01, 0x02, 0x03);

        var n2 = stream.Read(buf);
        n2.Should().Be(2);
        buf[..n2].ToArray().Should().Equal(0xAA, 0xBB);

        var n3 = stream.Read(buf);
        n3.Should().Be(0);
    }

    [Fact]
    public void GetFeature_returns_canned_feature_data_when_provided()
    {
        var stream = new FakeHidStream(reports: Array.Empty<byte[]>());
        stream.SetFeatureResponse(new byte[] { 0xF0, 0xF1, 0xF2 });

        var buf = new byte[3];
        stream.GetFeature(buf);

        buf.Should().Equal(0xF0, 0xF1, 0xF2);
        stream.GetFeatureCallCount.Should().Be(1);
    }

    [Fact]
    public void SetFeature_records_payloads_in_order()
    {
        var stream = new FakeHidStream(reports: Array.Empty<byte[]>());

        stream.SetFeature(new byte[] { 1, 2, 3 });
        stream.SetFeature(new byte[] { 4, 5 });

        stream.SetFeatureCalls.Should().HaveCount(2);
        stream.SetFeatureCalls[0].Should().Equal(1, 2, 3);
        stream.SetFeatureCalls[1].Should().Equal(4, 5);
    }

    [Fact]
    public void Dispose_flips_IsDisposed_flag()
    {
        var stream = new FakeHidStream(reports: Array.Empty<byte[]>());
        stream.IsDisposed.Should().BeFalse();

        stream.Dispose();

        stream.IsDisposed.Should().BeTrue();
    }
}
