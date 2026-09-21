using ApexMapper.Core.Calibration;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class CompiledProfileTests
{
    [Fact]
    public void Forza_profile_is_valid()
    {
        Assert.Null(DefaultProfiles.Forza().Validate());
    }

    [Fact]
    public void Analog_keys_are_the_calibratable_trigger_and_axis_keys()
    {
        var analog = DefaultProfiles.Forza().AnalogKeys(SensorMap.Default).ToList();
        Assert.Equal(4, analog.Count);
        Assert.Contains(DefaultProfiles.Key.W, analog);
        Assert.Contains(DefaultProfiles.Key.D, analog);
        Assert.DoesNotContain(DefaultProfiles.Key.Space, analog);
        Assert.DoesNotContain(DefaultProfiles.Key.Left, analog);
    }

    [Fact]
    public void Compiles_with_every_analog_key_calibrated()
    {
        var compiled = CompiledProfile.TryCompile(DefaultProfiles.Forza(), SensorMap.Default, Fixtures.Calibrations(), out var missing);
        Assert.NotNull(compiled);
        Assert.Empty(missing);
        Assert.Equal(4, compiled.AnalogKeys.Count);
        Assert.Equal([2, 3], compiled.NeededGroups);
        Assert.Equal(9, compiled.Keys.Count);
        Assert.Equal(3, compiled.Axes.Count);
        Assert.False(compiled.Keys.Single(k => k.Key.Key == DefaultProfiles.Key.Space).Key.AnalogDriven);
    }

    [Fact]
    public void Missing_calibration_names_the_keys_and_refuses()
    {
        var partial = new Dictionary<ScanCode, KeyCalibration> { [DefaultProfiles.Key.W] = Fixtures.W };
        var compiled = CompiledProfile.TryCompile(DefaultProfiles.Forza(), SensorMap.Default, partial, out var missing);
        Assert.Null(compiled);
        Assert.Equal(3, missing.Count);
        Assert.Contains(DefaultProfiles.Key.A, missing);
    }

    [Fact]
    public void An_invalid_calibration_counts_as_missing()
    {
        var calibrations = Fixtures.Calibrations();
        calibrations[DefaultProfiles.Key.A] = new KeyCalibration(800, 3800, 20, 99);
        var compiled = CompiledProfile.TryCompile(DefaultProfiles.Forza(), SensorMap.Default, calibrations, out var missing);
        Assert.Null(compiled);
        Assert.Equal([DefaultProfiles.Key.A], missing);
    }

    [Fact]
    public void Invalid_profiles_are_rejected_before_compiling()
    {
        var profile = DefaultProfiles.Forza() with
        {
            Keys = [new(new ScanCode(0x1D), Core.Bindings.PadTarget.ButtonA, Core.Response.Response.Linear, 0f, 0f)],
        };
        Assert.NotNull(profile.Validate());
        Assert.Throws<ArgumentException>(() => CompiledProfile.TryCompile(profile, SensorMap.Default, Fixtures.Calibrations(), out _));

        var duplicate = DefaultProfiles.Forza() with
        {
            Keys = [.. DefaultProfiles.Forza().Keys, new(DefaultProfiles.Key.A, Core.Bindings.PadTarget.ButtonB, Core.Response.Response.Linear, 0f, 0f)],
        };
        Assert.NotNull(duplicate.Validate());
    }
}
