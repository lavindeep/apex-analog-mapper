using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class LearnStepTests
{
    private static ushort[] Baseline()
    {
        var baseline = new ushort[70];
        Array.Fill(baseline, (ushort)850);
        baseline[27] = 8;
        return baseline;
    }

    [Fact]
    public void Finds_the_sensor_that_moved()
    {
        var learn = new LearnStep(Baseline());
        var reading = Baseline();
        reading[16] = 2000;
        reading[15] = 870;
        learn.Observe(reading);
        var result = learn.Result();
        Assert.Equal(LearnOutcome.Found, result.Outcome);
        Assert.Equal(16, result.SensorIndex);
        Assert.Equal(1150, result.Delta);
        Assert.True(learn.AnythingMoved());
    }

    [Fact]
    public void Nothing_moving_enough_is_reported()
    {
        var learn = new LearnStep(Baseline());
        var reading = Baseline();
        reading[16] = 1000;
        learn.Observe(reading);
        Assert.Equal(LearnOutcome.NothingMoved, learn.Result().Outcome);
        Assert.False(learn.AnythingMoved());
    }

    [Fact]
    public void Two_keys_pressed_is_ambiguous()
    {
        var learn = new LearnStep(Baseline());
        var reading = Baseline();
        reading[16] = 3000;
        reading[29] = 2500;
        learn.Observe(reading);
        var result = learn.Result();
        Assert.Equal(LearnOutcome.Ambiguous, result.Outcome);
        Assert.Equal(16, result.SensorIndex);
        Assert.Equal(29, result.SecondIndex);
    }

    [Theory]
    [InlineData(300, 0, LearnOutcome.Found)]
    [InlineData(299, 0, LearnOutcome.NothingMoved)]
    [InlineData(2000, 1000, LearnOutcome.Ambiguous)]
    [InlineData(2000, 999, LearnOutcome.Found)]
    public void Thresholds_are_exact_at_their_boundaries(int bestDelta, int secondDelta, LearnOutcome expected)
    {
        var learn = new LearnStep(Baseline());
        var reading = Baseline();
        reading[16] = (ushort)(850 + bestDelta);
        reading[29] = (ushort)(850 + secondDelta);
        learn.Observe(reading);
        Assert.Equal(expected, learn.Result().Outcome);
    }

    [Fact]
    public void A_short_reading_is_refused()
    {
        var learn = new LearnStep(Baseline());
        Assert.Throws<ArgumentException>(() => learn.Observe(new ushort[10]));
    }

    [Fact]
    public void Peak_is_kept_across_observations()
    {
        var learn = new LearnStep(Baseline());
        var reading = Baseline();
        reading[30] = 3500;
        learn.Observe(reading);
        learn.Observe(Baseline());
        Assert.Equal(30, learn.Result().SensorIndex);
        Assert.Throws<ArgumentException>(() => new LearnStep(new ushort[10]));
    }

    [Fact]
    public void Capture_export_round_trips()
    {
        var export = new CaptureExport("0.5.0-alpha.1", DateTimeOffset.Parse("2026-09-20T02:00:00Z"), 0x1642, "Apex Pro TKL Gen 3", "1.2.3",
            "27373de1-4206-11f1-b9e4-14ac60fcc13e", 65, 65,
            [new CapturedReply("rest", 2, "00" + new string('A', 128)), new CapturedReply("W held", 2, "00" + new string('B', 128))]);
        var back = CaptureExport.FromJson(export.ToJson(), out var error);
        Assert.Null(error);
        Assert.Equal(export with { Replies = [] }, back! with { Replies = [] });
        Assert.Equal(export.Replies, back.Replies);

        Assert.Null(CaptureExport.FromJson("garbage", out error));
        Assert.NotNull(error);
        Assert.Null(CaptureExport.FromJson(export.ToJson().Replace("\"version\": 1", "\"version\": 9"), out error));
        Assert.Contains("newer", error);
    }
}
