using ApexMapper.Core.Pipeline;
using ApexMapper.Output.ViGEm;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class ViGEmXboxOutputTests
{
    // These run on the macOS gate: constructing the output and the not-connected
    // guards must never touch the ViGEm driver, so they are provably safe off
    // Windows. The one runtime-driver assertion is Windows-guarded below.

    [Fact]
    public void Constructing_does_not_touch_the_driver()
    {
        var act = () => new ViGEmXboxOutput();

        act.Should().NotThrow();
        var output = new ViGEmXboxOutput();
        output.IsConnected.Should().BeFalse();
        output.LastError.Should().BeNull();
    }

    [Fact]
    public void Submit_when_not_connected_throws()
    {
        var output = new ViGEmXboxOutput();
        var state = new VirtualPadState { LeftTrigger = 1f };

        var act = () => output.Submit(state);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Zero_when_not_connected_throws()
    {
        var output = new ViGEmXboxOutput();

        var act = output.Zero;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Disconnect_when_never_connected_is_a_quiet_no_op()
    {
        var output = new ViGEmXboxOutput();

        var act = output.Disconnect;

        act.Should().NotThrow();
        output.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Disconnect_is_idempotent()
    {
        var output = new ViGEmXboxOutput();

        output.Disconnect();
        var again = output.Disconnect;

        again.Should().NotThrow();
        output.IsConnected.Should().BeFalse();
    }

    [DriverlessFact]
    public void Connect_without_the_driver_throws_a_descriptive_error_and_sets_LastError()
    {
        var output = new ViGEmXboxOutput();
        try
        {
            var act = output.Connect;
            act.Should().Throw<InvalidOperationException>().WithMessage("*ViGEmBus*");
            output.IsConnected.Should().BeFalse();
            output.LastError.Should().NotBeNullOrEmpty();
            output.LastError.Should().Contain("ViGEmBus");
        }
        finally
        {
            output.Disconnect();
        }
    }
}
