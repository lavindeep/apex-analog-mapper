using System.Reflection;
using ApexMapper.Core.Pipeline;
using ApexMapper.Output.ViGEm;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class Xbox360ReportPackerTests
{
    // Each VirtualPadState button field paired with the report property it must
    // drive. Exercised one-hot so a dropped or crossed wire cannot hide.
    private static readonly (string PadField, string ReportProperty)[] ButtonMap =
    {
        ("ButtonA", "A"),
        ("ButtonB", "B"),
        ("ButtonX", "X"),
        ("ButtonY", "Y"),
        ("ButtonLB", "LeftShoulder"),
        ("ButtonRB", "RightShoulder"),
        ("ButtonStart", "Start"),
        ("ButtonBack", "Back"),
        ("ButtonLS", "LeftThumb"),
        ("ButtonRS", "RightThumb"),
        ("ButtonGuide", "Guide"),
        ("DpadUp", "DpadUp"),
        ("DpadDown", "DpadDown"),
        ("DpadLeft", "DpadLeft"),
        ("DpadRight", "DpadRight"),
    };

    [Fact]
    public void Neutral_state_packs_to_all_zero()
    {
        var report = Xbox360ReportPacker.Pack(new VirtualPadState());

        report.LeftStickX.Should().Be(0);
        report.LeftStickY.Should().Be(0);
        report.RightStickX.Should().Be(0);
        report.RightStickY.Should().Be(0);
        report.LeftTrigger.Should().Be(0);
        report.RightTrigger.Should().Be(0);
        BoolProperties(report).Values.Should().OnlyContain(v => v == false);
    }

    [Fact]
    public void Stick_endpoints_map_to_full_symmetric_range()
    {
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = 1f }).LeftStickX.Should().Be(32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = -1f }).LeftStickX.Should().Be(-32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickY = 1f }).LeftStickY.Should().Be(32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickY = -1f }).LeftStickY.Should().Be(-32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightStickX = 1f }).RightStickX.Should().Be(32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightStickX = -1f }).RightStickX.Should().Be(-32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightStickY = 1f }).RightStickY.Should().Be(32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightStickY = -1f }).RightStickY.Should().Be(-32767);
    }

    [Fact]
    public void Trigger_endpoints_map_to_zero_and_full_byte()
    {
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = 0f }).LeftTrigger.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = 1f }).LeftTrigger.Should().Be(255);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightTrigger = 0f }).RightTrigger.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightTrigger = 1f }).RightTrigger.Should().Be(255);
    }

    [Fact]
    public void Midpoints_round_to_nearest()
    {
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = 0.5f }).LeftTrigger.Should().Be(128);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = 0.5f }).LeftStickX.Should().Be(16384);
    }

    [Fact]
    public void Finite_out_of_range_values_clamp()
    {
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = 2f }).LeftStickX.Should().Be(32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = -2f }).LeftStickX.Should().Be(-32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = 2f }).LeftTrigger.Should().Be(255);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = -2f }).LeftTrigger.Should().Be(0);
    }

    [Fact]
    public void Negative_stick_floor_never_reaches_short_min()
    {
        // The negative extreme clamps to -32767, one short of short.MinValue
        // (-32768), keeping the axis symmetric about zero: full-left and
        // full-right are equal magnitudes, so no curve or deadzone sees a
        // lopsided range. The lost LSB at the extreme is imperceptible.
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = -1000f }).LeftStickX
            .Should().Be(-32767);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = -1000f }).LeftStickX
            .Should().BeGreaterThan(short.MinValue);
    }

    [Fact]
    public void Nan_axis_and_trigger_pack_to_neutral()
    {
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = float.NaN }).LeftStickX.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightStickY = float.NaN }).RightStickY.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = float.NaN }).LeftTrigger.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { RightTrigger = float.NaN }).RightTrigger.Should().Be(0);
    }

    [Fact]
    public void Infinity_axis_and_trigger_pack_to_neutral()
    {
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = float.PositiveInfinity }).LeftStickX.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftStickX = float.NegativeInfinity }).LeftStickX.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = float.PositiveInfinity }).LeftTrigger.Should().Be(0);
        Xbox360ReportPacker.Pack(new VirtualPadState { LeftTrigger = float.NegativeInfinity }).LeftTrigger.Should().Be(0);
    }

    [Fact]
    public void Every_button_maps_one_to_one_with_no_drop_or_cross()
    {
        foreach (var (padField, reportProperty) in ButtonMap)
        {
            var state = new VirtualPadState();
            SetPadButton(ref state, padField, true);

            var props = BoolProperties(Xbox360ReportPacker.Pack(state));

            props[reportProperty].Should().BeTrue(
                $"{padField} must drive {reportProperty}");
            foreach (var other in props.Where(p => p.Key != reportProperty))
            {
                other.Value.Should().BeFalse(
                    $"{padField} must not drive {other.Key}");
            }
        }
    }

    [Fact]
    public void Report_exposes_exactly_the_fifteen_buttons()
    {
        BoolProperties(default).Keys.Should().BeEquivalentTo(ButtonMap.Select(m => m.ReportProperty));
    }

    private static void SetPadButton(ref VirtualPadState state, string field, bool value)
    {
        // VirtualPadState is a mutable struct with public bool fields; box, set,
        // unbox so the one-hot assignment is data-driven from ButtonMap.
        object boxed = state;
        typeof(VirtualPadState).GetField(field)!.SetValue(boxed, value);
        state = (VirtualPadState)boxed;
    }

    private static IReadOnlyDictionary<string, bool> BoolProperties(Xbox360Report report) =>
        typeof(Xbox360Report)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool))
            .ToDictionary(p => p.Name, p => (bool)p.GetValue(report)!);
}
