using ApexMapper.Core.Ramps;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Ramps;

public class RampTests
{
    [Fact]
    public void Starts_at_rest()
    {
        new Ramp(pressMs: 120f, releaseMs: 80f).Value.Should().Be(0f);
    }

    [Fact]
    public void Press_advances_linearly_over_press_ms()
    {
        var ramp = new Ramp(pressMs: 100f, releaseMs: 100f);
        ramp.Update(pressed: true, dtMs: 50f);
        ramp.Value.Should().BeApproximately(0.5f, 1e-4f);
        ramp.Update(pressed: true, dtMs: 50f);
        ramp.Value.Should().BeApproximately(1f, 1e-4f);
    }

    [Fact]
    public void Release_returns_linearly_over_release_ms()
    {
        var ramp = new Ramp(pressMs: 100f, releaseMs: 200f);
        ramp.Update(pressed: true, dtMs: 100f);
        ramp.Value.Should().BeApproximately(1f, 1e-4f);
        ramp.Update(pressed: false, dtMs: 100f);
        ramp.Value.Should().BeApproximately(0.5f, 1e-4f);
        ramp.Update(pressed: false, dtMs: 100f);
        ramp.Value.Should().BeApproximately(0f, 1e-4f);
    }

    [Fact]
    public void Release_snap_when_release_ms_is_zero()
    {
        var ramp = new Ramp(pressMs: 100f, releaseMs: 0f);
        ramp.Update(pressed: true, dtMs: 100f);
        ramp.Value.Should().Be(1f);
        ramp.Update(pressed: false, dtMs: 1f);
        ramp.Value.Should().Be(0f);
    }

    [Fact]
    public void Press_snap_when_press_ms_is_zero()
    {
        var ramp = new Ramp(pressMs: 0f, releaseMs: 100f);
        ramp.Update(pressed: true, dtMs: 1f);
        ramp.Value.Should().Be(1f);
    }

    [Fact]
    public void Interruption_during_press_reverses_from_current_value()
    {
        var ramp = new Ramp(pressMs: 100f, releaseMs: 100f);
        ramp.Update(pressed: true, dtMs: 30f);
        ramp.Value.Should().BeApproximately(0.3f, 1e-4f);
        ramp.Update(pressed: false, dtMs: 15f);
        ramp.Value.Should().BeApproximately(0.15f, 1e-4f);
    }

    [Fact]
    public void Rejects_negative_durations()
    {
        Action a = () => _ = new Ramp(pressMs: -1f, releaseMs: 100f);
        a.Should().Throw<ArgumentException>();
        Action b = () => _ = new Ramp(pressMs: 100f, releaseMs: -1f);
        b.Should().Throw<ArgumentException>();
    }
}
