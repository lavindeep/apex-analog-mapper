using ApexMapper.Core.Engine;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class RateStateTests
{
    [Fact]
    public void Deflection_grows_with_depth_times_time_and_clamps()
    {
        var state = new RateState();
        Assert.Equal(0.1f, state.Step(0.5f, 30f, 150f, 100f), 0.0001f);
        Assert.Equal(0.2f, state.Step(0.5f, 30f, 150f, 100f), 0.0001f);
        for (var i = 0; i < 100; i++)
        {
            state.Step(1f, 10f, 150f, 100f);
        }
        Assert.Equal(1f, state.Deflection);
        for (var i = 0; i < 100; i++)
        {
            state.Step(-1f, 10f, 150f, 100f);
        }
        Assert.Equal(-1f, state.Deflection);
    }

    [Fact]
    public void Release_returns_to_centre_over_the_return_time()
    {
        var state = new RateState();
        state.Step(1f, 150f, 150f, 100f);
        Assert.Equal(1f, state.Deflection);
        Assert.Equal(0.5f, state.Step(0f, 50f, 150f, 100f), 0.0001f);
        Assert.Equal(0f, state.Step(0f, 60f, 150f, 100f));
        Assert.Equal(0f, state.Step(0f, 60f, 150f, 100f));
    }

    [Fact]
    public void Zero_return_time_snaps_to_centre()
    {
        var state = new RateState();
        state.Step(-1f, 75f, 150f, 0f);
        Assert.Equal(-0.5f, state.Deflection, 0.0001f);
        Assert.Equal(0f, state.Step(0f, 1f, 150f, 0f));
    }
}
