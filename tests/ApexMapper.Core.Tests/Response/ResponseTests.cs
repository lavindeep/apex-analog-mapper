using ApexMapper.Core.Response;
using Xunit;

namespace ApexMapper.Core.Tests.Response;

public class ResponseTests
{
    public static IEnumerable<object[]> Curves() =>
    [
        [Core.Response.Response.Default],
        [Core.Response.Response.Create(1f, 1f, 0f)],
        [Core.Response.Response.Create(2f, 0.8f, 0.15f)],
    ];

    [Theory]
    [MemberData(nameof(Curves))]
    public void Curves_are_monotone_from_zero_to_one(Core.Response.Response response)
    {
        var last = -1f;
        for (var i = 0; i <= 100; i++)
        {
            var value = response.Map(i / 100f);
            Assert.True(value >= last, $"not monotone at {i}");
            last = value;
        }
        Assert.Equal(0f, response.Map(0f));
        Assert.Equal(1f, response.Map(1f));
    }

    [Fact]
    public void The_exponent_bends_the_curve_at_mid_travel()
    {
        Assert.Equal(0.5f, Core.Response.Response.Create(1f, 1f, 0f).Map(0.5f));
        Assert.Equal(0.3299f, Core.Response.Response.Create(1.6f, 1f, 0f).Map(0.5f), 0.001f);
        Assert.Equal(0.8409f, Core.Response.Response.Default.Map(0.5f), 0.001f);
    }

    [Fact]
    public void Deadzone_renormalises_instead_of_stepping()
    {
        var response = Core.Response.Response.Create(1f, 1f, 0.1f);
        Assert.Equal(0f, response.Map(0.1f));
        var justAbove = response.Map(0.101f);
        Assert.True(justAbove > 0f && justAbove < 0.01f);
        Assert.Equal(1f, response.Map(1f));
    }

    [Fact]
    public void Saturation_reaches_full_before_full_travel()
    {
        var response = Core.Response.Response.Create(1f, 0.8f, 0f);
        Assert.Equal(1f, response.Map(0.8f));
        Assert.Equal(1f, response.Map(0.95f));
        Assert.InRange(response.Map(0.4f), 0.499f, 0.501f);
    }

    [Fact]
    public void Validation_rejects_bad_parameters()
    {
        Assert.NotNull(Core.Response.Response.Validate(0.1f, 1f, 0f));
        Assert.NotNull(Core.Response.Response.Validate(1f, 1.1f, 0f));
        Assert.NotNull(Core.Response.Response.Validate(1f, 0.5f, 0.5f));
        Assert.NotNull(Core.Response.Response.Validate(float.NaN, 1f, 0f));
        Assert.NotNull(Core.Response.Response.Validate(1f, float.NaN, 0f));
        Assert.NotNull(Core.Response.Response.Validate(1f, 1f, float.NaN));
        Assert.NotNull(Core.Response.Response.Validate(1f, 1f, -0.1f));
        Assert.Null(Core.Response.Response.Validate(2f, 0.9f, 0.05f));
        Assert.Throws<ArgumentException>(() => Core.Response.Response.Create(0.1f, 1f, 0f));
    }

    [Fact]
    public void Non_finite_depth_maps_to_zero()
    {
        Assert.Equal(0f, Core.Response.Response.Default.Map(float.NaN));
    }
}
