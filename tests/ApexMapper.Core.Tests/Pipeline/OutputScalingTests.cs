using ApexMapper.Core.Pipeline;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Pipeline;

public class OutputScalingTests
{
    [Theory]
    [InlineData(0f, (byte)0)]
    [InlineData(0.5f, (byte)128)]
    [InlineData(1f, (byte)255)]
    [InlineData(-0.5f, (byte)0)]
    [InlineData(1.5f, (byte)255)]
    public void Trigger_scales_to_byte(float input, byte expected)
    {
        OutputScaling.ToTrigger(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(0f, (short)0)]
    [InlineData(1f, (short)32767)]
    [InlineData(-1f, (short)-32767)]
    [InlineData(0.5f, (short)16384)]
    [InlineData(-0.5f, (short)-16384)]
    [InlineData(2f, (short)32767)]
    [InlineData(-2f, (short)-32767)]
    public void Stick_scales_to_signed_short(float input, short expected)
    {
        OutputScaling.ToStick(input).Should().Be(expected);
    }
}
