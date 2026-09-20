using ApexMapper.Core.Engine;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class RampTests
{
    [Fact]
    public void Press_takes_the_full_duration_from_zero()
    {
        var ramp = new Ramp();
        for (var i = 0; i < 10; i++)
        {
            ramp.Update(1f, 10f, 100f);
        }
        Assert.Equal(1f, ramp.Value, 0.0001f);
    }

    [Fact]
    public void Release_from_a_partial_value_takes_a_proportional_time()
    {
        var ramp = new Ramp();
        ramp.Seed(0.3f);
        ramp.Update(0f, 15f, 100f);
        Assert.Equal(0.15f, ramp.Value, 0.0001f);
        ramp.Update(0f, 15f, 100f);
        Assert.Equal(0f, ramp.Value);
    }

    [Fact]
    public void Interruption_reverses_from_the_current_value()
    {
        var ramp = new Ramp();
        ramp.Update(1f, 30f, 100f);
        Assert.Equal(0.3f, ramp.Value, 0.0001f);
        ramp.Update(0f, 15f, 100f);
        Assert.Equal(0.15f, ramp.Value, 0.0001f);
    }

    [Fact]
    public void Zero_duration_snaps()
    {
        var ramp = new Ramp();
        ramp.Update(1f, 1f, 0f);
        Assert.Equal(1f, ramp.Value);
        ramp.Update(0f, 1f, 0f);
        Assert.Equal(0f, ramp.Value);
    }

    [Fact]
    public void Seed_then_ramp_starts_from_the_seed()
    {
        var ramp = new Ramp();
        ramp.Seed(0.7f);
        ramp.Update(1f, 5f, 50f);
        Assert.Equal(0.8f, ramp.Value, 0.0001f);
    }

    [Fact]
    public void Never_overshoots_the_target()
    {
        var ramp = new Ramp();
        ramp.Seed(0.95f);
        ramp.Update(1f, 100f, 100f);
        Assert.Equal(1f, ramp.Value);
    }
}
