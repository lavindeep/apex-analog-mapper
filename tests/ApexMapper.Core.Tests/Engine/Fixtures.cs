using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;

namespace ApexMapper.Core.Tests.Engine;

/// <summary>The maintainer's board as measured in stage 0. Snapshot clocks tick in milliseconds.</summary>
internal static class Fixtures
{
    public static readonly KeyCalibration W = KeyCalibration.Create(878, 4095, 20, 16);
    public static readonly KeyCalibration A = KeyCalibration.Create(843, 3558, 20, 29);
    public static readonly KeyCalibration S = KeyCalibration.Create(847, 3559, 20, 30);
    public static readonly KeyCalibration D = KeyCalibration.Create(850, 3623, 20, 31);

    public static Dictionary<ScanCode, KeyCalibration> Calibrations() => new()
    {
        [DefaultProfiles.Key.W] = W,
        [DefaultProfiles.Key.A] = A,
        [DefaultProfiles.Key.S] = S,
        [DefaultProfiles.Key.D] = D,
    };

    /// <summary>A snapshot stamped at the given millisecond with groups 2 and 3 at rest, optionally overriding raw counts by sensor index.</summary>
    public static SensorSnapshot Snapshot(long timestampMs, params (int Index, ushort Raw)[] overrides) =>
        Snapshot(timestampMs, [2, 3], overrides);

    public static SensorSnapshot Snapshot(long timestampMs, int[] groups, params (int Index, ushort Raw)[] overrides)
    {
        var snapshot = new SensorSnapshot(ticksPerMs: 1);
        snapshot.Begin(timestampMs);
        var raw = new ushort[14];
        var filtered = new ushort[14];
        foreach (var group in groups)
        {
            Array.Fill(raw, (ushort)850);
            raw[2] = 878;
            if (group == 3)
            {
                raw[1] = 843;
                raw[2] = 847;
                raw[3] = 850;
            }
            foreach (var (index, value) in overrides)
            {
                if (SensorMap.GroupOf(index) == group)
                {
                    raw[SensorMap.SlotOf(index)] = value;
                }
            }
            snapshot.SetGroup(group, raw, filtered);
        }
        return snapshot;
    }

    /// <summary>Raw count for a depth on a calibration, inverse of the normaliser (nearest count).</summary>
    public static ushort CountFor(KeyCalibration cal, float depth) =>
        (ushort)MathF.Round(cal.Rest + cal.NoiseBand + depth * (cal.Span - cal.NoiseBand));

    /// <summary>A count only lands within half a count of the requested depth, which is a few stick units.</summary>
    public static void AssertStick(float expectedValue, short actual) =>
        Xunit.Assert.InRange(actual, Core.Engine.PadReport.PackStick(expectedValue) - 8, Core.Engine.PadReport.PackStick(expectedValue) + 8);

    public static void AssertTrigger(float expectedValue, byte actual) =>
        Xunit.Assert.InRange(actual, Core.Engine.PadReport.PackTrigger(expectedValue) - 1, Core.Engine.PadReport.PackTrigger(expectedValue) + 1);
}
