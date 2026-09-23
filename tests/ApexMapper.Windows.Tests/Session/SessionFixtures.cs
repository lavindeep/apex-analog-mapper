using System.Diagnostics;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using ApexMapper.Windows.Session;
using ApexMapper.Windows.Tests.Hid;

namespace ApexMapper.Windows.Tests.Session;

/// <summary>The Forza profile calibrated against the rest captures, and helpers to drive keys and sensors.</summary>
internal static class SessionFixtures
{
    public const string GamePath = @"C:\Games\ForzaHorizon6\ForzaHorizon6.exe";
    public const int Span = 2000;

    public static readonly Guid KeyboardId = new("5a1d0c3e-0000-4000-8000-000000001614");
    public static readonly KeyboardInfo Board = new(KeyboardId, 0x1614, "Apex Pro TKL", Known: true, HasVendorInterface: true);

    public static readonly ScanCode W = DefaultProfiles.Key.W;
    public static readonly ScanCode Space = DefaultProfiles.Key.Space;

    /// <summary>W, A, S, D calibrated with the rest capture as rest and <see cref="Span"/> counts of upward travel.</summary>
    public static IReadOnlyDictionary<ScanCode, KeyCalibration> Calibrations()
    {
        var map = SensorMap.Default;
        var result = new Dictionary<ScanCode, KeyCalibration>();
        foreach (var key in new[] { DefaultProfiles.Key.W, DefaultProfiles.Key.A, DefaultProfiles.Key.S, DefaultProfiles.Key.D })
        {
            map.TryGetSensorIndex(key, out var index);
            var rest = RestRaw(index);
            result[key] = KeyCalibration.Create(rest, rest + Span, KeyCalibration.DefaultNoiseBand, index);
        }
        return result;
    }

    public static CompiledProfile Forza() =>
        CompiledProfile.TryCompile(DefaultProfiles.Forza(), SensorMap.Default, Calibrations(), out _)!;

    public static SessionRequest Request() => new(KeyboardId, GamePath, Forza(), Fixtures.Signatures(2, 3));

    /// <summary>The rest captures, one read a millisecond so the poller does not spin a core.</summary>
    public static FakeVendorStream SlowStream() => new()
    {
        OnRead = (_, command, selector) =>
        {
            Thread.Sleep(1);
            return FakeVendorStream.DefaultReply(command, selector);
        },
    };

    /// <summary>The raw count the rest capture holds for a sensor.</summary>
    public static ushort RestRaw(int sensorIndex)
    {
        var raw = new ushort[SensorProtocol.SensorsPerGroup];
        SensorProtocol.ParseGroup(Fixtures.RestGroup(SensorMap.GroupOf(sensorIndex)), raw, new ushort[SensorProtocol.SensorsPerGroup]);
        return raw[SensorMap.SlotOf(sensorIndex)];
    }

    /// <summary>Groups 2 and 3 at rest, stamped in Stopwatch ticks, with raw overrides by sensor index.</summary>
    public static SensorSnapshot Snapshot(long stampTicks, params (int Index, ushort Raw)[] overrides)
    {
        var snapshot = new SensorSnapshot();
        Publish(snapshot, stampTicks, overrides);
        return snapshot;
    }

    /// <summary>Publishes groups 2 and 3 into a shared snapshot, as the poller would.</summary>
    public static void Publish(SensorSnapshot snapshot, long stampTicks, params (int Index, ushort Raw)[] overrides)
    {
        var working = new SensorSnapshot();
        working.Begin(stampTicks);
        foreach (var group in new[] { 2, 3 })
        {
            var raw = new ushort[SensorProtocol.SensorsPerGroup];
            var filtered = new ushort[SensorProtocol.SensorsPerGroup];
            SensorProtocol.ParseGroup(Fixtures.RestGroup(group), raw, filtered);
            foreach (var (index, value) in overrides)
            {
                if (SensorMap.GroupOf(index) == group)
                {
                    raw[SensorMap.SlotOf(index)] = value;
                }
            }
            working.SetGroup(group, raw, filtered);
        }
        working.Stamp(stampTicks);
        snapshot.Publish(working);
    }

    /// <summary>The sensor index and raw count that put an analog key at a depth.</summary>
    public static (int Index, ushort Raw) Depth(ScanCode key, float depth)
    {
        var cal = Calibrations()[key];
        return (cal.SensorIndex, (ushort)MathF.Round(cal.Rest + cal.NoiseBand + depth * (Span - cal.NoiseBand)));
    }

    public static User32.KBDLLHOOKSTRUCT Key(ScanCode key) => new()
    {
        scanCode = (uint)(key.Value & 0xFF),
        flags = key.Value > 0xFF ? User32.LLKHF_EXTENDED : 0,
    };

    public static ForegroundInfo GameInFront() => new(100, 4242, GamePath, IsGame: true, Elevation.Visible, Unwrapped: false);

    public static ForegroundInfo DesktopInFront() => new(300, 7, @"C:\Windows\explorer.exe", IsGame: false, Elevation.Visible, Unwrapped: false);

    public static long Now() => Stopwatch.GetTimestamp();
}

/// <summary>A foreground source the test moves by hand, keeping the tracker's order: on gain the handlers run before the flag flips, on loss after it drops.</summary>
internal sealed class FakeForeground(ForegroundFlag flag) : IForegroundSource
{
    private ForegroundInfo _current = ForegroundInfo.None;

    public string? GamePath { get; set; }

    public ForegroundInfo Current => Volatile.Read(ref _current);

    public event Action<ForegroundInfo>? Changed;

    public int HandlerFaults => 0;

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    /// <summary>Its thread ended early, as a tracker whose loop failed.</summary>
    public bool Died { get; set; }

    public bool IsRunning => Started && !Stopped && !Died;

    public void Start() => Started = true;

    public bool Stop()
    {
        Stopped = true;
        flag.IsGameForeground = false;
        return true;
    }

    /// <summary>The game comes to the front. The flag goes up only for one whose input the app can see, as the tracker's does.</summary>
    public void Gain(Elevation elevation = Elevation.Visible)
    {
        var info = SessionFixtures.GameInFront() with { Elevation = elevation };
        Volatile.Write(ref _current, info);
        Changed?.Invoke(info);
        flag.IsGameForeground = info.GameHasFocus;
    }

    public void Lose()
    {
        var info = SessionFixtures.DesktopInFront();
        Volatile.Write(ref _current, info);
        flag.IsGameForeground = false;
        Changed?.Invoke(info);
    }

    public void Dispose() => Stop();
}

internal sealed class FakeGame : IGameProcess
{
    private Action? _exited;

    public bool Disposed { get; private set; }

    public void OnExit(Action exited) => _exited = exited;

    public void Exit() => ThreadPool.QueueUserWorkItem(_ => _exited?.Invoke());

    public void Dispose() => Disposed = true;
}

internal sealed class FakePower : IPowerEvents
{
    public event Action? SleepOrWake;

    public int Subscribers => SleepOrWake?.GetInvocationList().Length ?? 0;

    public void Raise() => SleepOrWake?.Invoke();
}

/// <summary>
/// Raw Input as the session sees it. The test sets the newest event time by hand. While
/// <see cref="Hold"/> is in effect, the health check's liveness read blocks, which holds
/// the session thread wherever the test needs it held.
/// </summary>
internal sealed class FakeRawInput : IRawInputActivity
{
    private readonly ManualResetEventSlim _release = new(true);
    private int _time;
    private int _running = 1;
    private int _held;

    public uint LastEventTime
    {
        get => Throws ? throw new InvalidOperationException("Raw Input read failed.") : (uint)Volatile.Read(ref _time);
        set => Volatile.Write(ref _time, (int)value);
    }

    /// <summary>Reading the event time throws, as a watchdog check failing on every tick would.</summary>
    public bool Throws { get; set; }

    public bool IsRunning
    {
        get
        {
            if (!_release.IsSet)
            {
                Volatile.Write(ref _held, 1);
                _release.Wait();
                Volatile.Write(ref _held, 0);
            }
            return Volatile.Read(ref _running) != 0;
        }
    }

    public bool Running
    {
        set => Volatile.Write(ref _running, value ? 1 : 0);
    }

    /// <summary>The session thread is inside a held health check.</summary>
    public bool Held => Volatile.Read(ref _held) != 0;

    public void Hold() => _release.Reset();

    public void Release() => _release.Set();
}
