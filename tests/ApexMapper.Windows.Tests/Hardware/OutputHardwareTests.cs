using System.Diagnostics;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Response;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;
using ApexMapper.Windows.Tests.Hid;
using ApexMapper.Windows.Tests.Native;
using ApexMapper.Windows.Tests.Session;
using ApexMapper.Windows.Timing;
using Xunit;
using static ApexMapper.Windows.Tests.Session.SessionFixtures;

namespace ApexMapper.Windows.Tests.Hardware;

/// <summary>
/// Against ViGEmBus on the maintainer's PC. The pad is real; the keyboard, game and
/// foreground are the session tests' fakes, so these need no Apex Pro. Only the kill
/// test injects keys: one W is typed into the foreground window after the child dies.
/// </summary>
[Collection(ProcessSingletons.Name)]
public class OutputHardwareTests
{
    private const ushort WScan = 0x11;

    private static readonly ScanCode CapsLock = new(0x3A);

    private static readonly ushort AllReadableButtons = Enum.GetValues<PadTarget>()
        .Where(t => t.IsButton() && t != PadTarget.Guide)
        .Aggregate((ushort)0, (bits, t) => (ushort)(bits | t.ButtonBit()));

    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    [HardwareFact]
    public void The_driver_service_is_running_on_this_pc()
    {
        Assert.Equal(DriverState.Running, DriverStatus.Query());
    }

    /// <summary>XInput never reports the guide button, so it is left out of the round trip.</summary>
    [HardwareFact]
    public void A_connected_pad_reads_neutral_round_trips_full_scale_and_is_gone_after_unplug()
    {
        using var pad = VirtualPad.Connect(TestContext.Current.CancellationToken);
        Assert.True(pad.TryReadBack(out var state, out _));
        Assert.Equal(PadReport.Neutral, state);

        var full = new PadReport(PadReport.StickMax, (short)-PadReport.StickMax, (short)-PadReport.StickMax, PadReport.StickMax, 255, 255, AllReadableButtons);
        var mirrored = new PadReport((short)-PadReport.StickMax, PadReport.StickMax, PadReport.StickMax, (short)-PadReport.StickMax, 1, 128, 0);
        foreach (var report in new[] { full, mirrored })
        {
            Assert.True(pad.TrySubmit(report, Stopwatch.GetTimestamp()));
            Assert.True(SpinWait.SpinUntil(() => pad.TryReadBack(out var s, out _) && s == report, 500), $"readback of {report}");
            Thread.Sleep(3);
        }

        var clock = Stopwatch.StartNew();
        Assert.True(pad.Unplug());
        Assert.True(SpinWait.SpinUntil(() => !pad.IsPresent(), HardwareThresholds.KillPadGoneMs), "pad still present after unplug");
        Assert.Null(pad.UnplugError);
        Assert.True(clock.ElapsedMilliseconds < HardwareThresholds.KillPadGoneMs, $"unplug took {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The engine drives D through its travel and back every 200 ms from a synthetic
    /// snapshot published every millisecond, so the stick changes on every tick. This
    /// thread spins on XInput, counting packets and timing each from the engine tick
    /// that sent it.
    /// </summary>
    [HardwareFact]
    public void Loopback_changes_the_pad_at_least_400_times_a_second_and_tick_to_readback_p99_is_under_the_threshold()
    {
        var snapshot = new SensorSnapshot();
        using var pad = VirtualPad.Connect(TestContext.Current.CancellationToken);
        var engine = new EngineLoop(new Mapper(Forza(), new KeyStateStore()), snapshot, pad, new ForegroundFlag { IsGameForeground = true });
        using var publisher = new SweepPublisher(snapshot, DefaultProfiles.Key.D, periodMs: 200);
        publisher.Start();
        engine.Start();
        Thread.Sleep(300);

        var latencies = new List<double>(4000);
        pad.TryReadBack(out _, out var lastPacket);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            if (pad.TryReadBack(out _, out var packet) && packet != lastPacket)
            {
                latencies.Add(Ms(Stopwatch.GetTimestamp() - pad.LastSubmitTicks));
                lastPacket = packet;
            }
        }
        var seconds = clock.Elapsed.TotalSeconds;
        Assert.True(engine.Stop());
        publisher.Stop();

        var rate = latencies.Count / seconds;
        var p50 = Percentile(latencies, 0.5);
        var p99 = Percentile(latencies, 0.99);
        TestContext.Current.SendDiagnosticMessage($"loopback: {rate:F0} packets/s, tick to readback p50 {p50:F3} ms, p99 {p99:F3} ms, max {latencies.Max():F3} ms");
        Assert.Null(engine.Fault);
        Assert.True(rate >= HardwareThresholds.XInputPacketsPerSecondMin, $"{rate:F0} packets/s.");
        Assert.True(rate <= 1000 / VirtualPad.MinSubmitIntervalMs, $"{rate:F0} packets/s is above the submit floor's ceiling.");
        Assert.True(p99 < HardwareThresholds.ReadbackP99Ms, $"p50 {p50:F3} ms, p99 {p99:F3} ms.");
    }

    /// <summary>
    /// The session with the real pad, whose engine gets stuck inside a submit that has
    /// reached the driver. The watchdog must take the pad, zero and unplug it within
    /// the threshold, and the stuck call's eventual return must not reach the driver.
    /// </summary>
    [HardwareFact]
    public async Task A_wedged_engine_is_unplugged_by_the_watchdog_within_the_threshold()
    {
        using var release = new ManualResetEventSlim(false);
        WedgingDriver? driver = null;
        using var rig = new LiveSession(cancel => VirtualPad.Connect(() => driver = new WedgingDriver(new ViGEmDriver(), release), cancel), SlowStream);
        Assert.Null(await rig.Session.StartAsync(Request()));
        rig.Foreground!.Gain();
        var slot = (uint)driver!.UserIndex;
        driver.Arm();

        rig.Session.Hook!.Handle(User32.WM_KEYDOWN, Key(Space));

        Assert.True(driver.Wedged.Wait(1000, TestContext.Current.CancellationToken), "the engine never submitted");
        Assert.True(SpinWait.SpinUntil(() => XInput.XInputGetState(slot, out _) == XInput.ERROR_DEVICE_NOT_CONNECTED, 2000), "pad still present");
        var goneAfter = Ms(Stopwatch.GetTimestamp() - driver.WedgedAtTicks);
        Assert.True(SpinWait.SpinUntil(() => rig.Session.State == SessionState.Idle, 2000), "session still running");
        var engineCallsBefore = driver.EngineCalls;
        release.Set();
        Thread.Sleep(100);

        TestContext.Current.SendDiagnosticMessage($"wedge: pad gone {goneAfter:F0} ms after the engine stuck");
        Assert.True(goneAfter < HardwareThresholds.WatchdogMs, $"pad gone {goneAfter:F0} ms after the stall.");
        Assert.Equal(EndReason.EngineStalled, rig.Session.LastEnd!.Reason);
        Assert.Equal(engineCallsBefore, driver.EngineCalls);
    }

    /// <summary>
    /// A child process with a pad at full trigger and a hook that swallows W is killed.
    /// This process's own hook, installed first so the child's runs ahead of it, is the
    /// witness: it sees no W while the child lives and both W events once it is dead.
    /// Types one W into the foreground window.
    /// </summary>
    [HardwareFact]
    public void Killing_the_process_unplugs_its_pad_and_removes_its_hook()
    {
        using var observer = new KeyboardHook(new KeyStateStore(), new HookPolicy(), new ForegroundFlag(), []);
        observer.Start();
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ApexMapper.Harness.exe"), "kill-child")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var line = child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.True(line.Wait(10_000, TestContext.Current.CancellationToken), "the child never reported its slot");
            Assert.StartsWith("slot ", line.Result);
            var slot = uint.Parse(line.Result![5..]);
            Assert.True(SpinWait.SpinUntil(() => XInput.XInputGetState(slot, out var s) == XInput.ERROR_SUCCESS && s.Gamepad.bRightTrigger == 255, 1000));

            var seen = observer.EventCount;
            Assert.True(Injector.SendScanCode(WScan, true));
            Assert.True(Injector.SendScanCode(WScan, false));
            Thread.Sleep(200);
            Assert.Equal(seen, observer.EventCount);

            var clock = Stopwatch.StartNew();
            child.Kill();
            Assert.True(SpinWait.SpinUntil(() => XInput.XInputGetState(slot, out _) == XInput.ERROR_DEVICE_NOT_CONNECTED, HardwareThresholds.KillPadGoneMs),
                $"pad still present {clock.ElapsedMilliseconds} ms after TerminateProcess");
            var goneAfter = clock.ElapsedMilliseconds;
            child.WaitForExit(2000);

            seen = observer.EventCount;
            Assert.True(Injector.SendScanCode(WScan, true));
            Assert.True(Injector.SendScanCode(WScan, false));
            Assert.True(SpinWait.SpinUntil(() => observer.EventCount >= seen + 2, 1000), "W did not reach this process after the child died");
            TestContext.Current.SendDiagnosticMessage($"kill: pad gone {goneAfter} ms after TerminateProcess");
        }
        finally
        {
            Injector.SendScanCode(WScan, false);
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }

    /// <summary>
    /// The whole session against the real pad for <see cref="SoakFactAttribute.Minutes"/>.
    /// The profile is one binding, Caps Lock to the right trigger, so the hook swallows only
    /// Caps Lock. Any pad movement makes Windows send gamepad navigation keys to apps that
    /// take them; a trigger sends the fewest. The sensor stream sweeps Caps Lock through
    /// its travel once a second. After a minute of warm-up, working set, handles, GC pauses
    /// and gen 2 collections must stay flat, and the pad must read neutral once the sweep
    /// stops.
    /// </summary>
    [SoakFact]
    public async Task A_long_session_keeps_memory_handles_and_gc_flat_and_ends_with_every_key_at_zero()
    {
        SensorMap.Default.TryGetSensorIndex(CapsLock, out var capsIndex);
        var capsRest = RestRaw(capsIndex);
        var calibrations = new Dictionary<ScanCode, KeyCalibration>
        {
            [CapsLock] = KeyCalibration.Create(capsRest, capsRest + Span, KeyCalibration.DefaultNoiseBand, capsIndex),
        };
        var profile = CompiledProfile.TryCompile(
            new Profile("soak", "Soak", Keys: [new(CapsLock, PadTarget.RightTrigger, Response.Linear, 0f, 0f)], Axes: []),
            SensorMap.Default,
            calibrations,
            out _)!;
        var stream = new SweepStream(calibrations[CapsLock]);
        using var rig = new LiveSession(VirtualPad.Connect, () => stream);
        Assert.Null(await rig.Session.StartAsync(new SessionRequest(KeyboardId, GamePath, profile, Fixtures.Signatures(3))));
        rig.Foreground!.Gain();
        var process = Process.GetCurrentProcess();
        var total = TimeSpan.FromMinutes(SoakFactAttribute.Minutes);
        var warmUp = TimeSpan.FromMinutes(1);
        var clock = Stopwatch.StartNew();
        long workingSet = 0, pauseTicks = 0;
        int handles = 0, gen2 = 0;
        var measuring = false;
        while (clock.Elapsed < total)
        {
            Thread.Sleep(250);
            if (!measuring && clock.Elapsed >= warmUp)
            {
                measuring = true;
                process.Refresh();
                workingSet = process.WorkingSet64;
                handles = process.HandleCount;
                pauseTicks = GC.GetTotalPauseDuration().Ticks;
                gen2 = GC.CollectionCount(2);
                clock.Restart();
                total -= warmUp;
            }
        }
        var measuredSeconds = clock.Elapsed.TotalSeconds;
        process.Refresh();
        var workingSetGrowth = (process.WorkingSet64 - workingSet) / (1024d * 1024d);
        var handleGrowth = process.HandleCount - handles;
        var pauseMsPerSecond = TimeSpan.FromTicks(GC.GetTotalPauseDuration().Ticks - pauseTicks).TotalMilliseconds / measuredSeconds;
        var gen2Growth = GC.CollectionCount(2) - gen2;

        stream.Rest();
        var status = rig.Session.Status();
        var neutral = SpinWait.SpinUntil(() => rig.Pad?.TryReadBack(out var s, out _) == true && s == PadReport.Neutral, 2000);
        await rig.Session.StopAsync(EndReason.UserStop);

        TestContext.Current.SendDiagnosticMessage(
            $"soak {measuredSeconds / 60:F1} min: working set {workingSetGrowth:+0.0;-0.0} MB, handles {handleGrowth:+0;-0}, GC pause {pauseMsPerSecond:F3} ms/s, gen 2 {gen2Growth}, submits {status.SubmitCount}");
        Assert.True(neutral, "the pad did not return to neutral");
        Assert.True(status.SubmitCount > 1000, $"only {status.SubmitCount} submits: the sweep did not drive the pad.");
        Assert.InRange(workingSetGrowth, -1000, 10);
        Assert.InRange(handleGrowth, -1000, 20);
        Assert.True(pauseMsPerSecond < 5, $"{pauseMsPerSecond:F3} ms of GC pause per second.");
        Assert.Equal(0, gen2Growth);
        Assert.Equal(0, status.HookReinstalls);
        Assert.Equal(EndReason.UserStop, rig.Session.LastEnd!.Reason);
    }

    /// <summary>A session with the real pad and fakes for everything else a session needs.</summary>
    private sealed class LiveSession : IDisposable
    {
        private readonly KeyboardDiscovery _keyboards = new(() => [Board]);
        private VirtualPad? _pad;

        public LiveSession(Func<CancellationToken, VirtualPad> connectPad, Func<IVendorStream?> openSensor)
        {
            _keyboards.Refresh();
            Session = new MappingSession(new SessionServices
            {
                Keyboards = _keyboards,
                RawInput = new FakeRawInput(),
                Power = new FakePower(),
                ConnectPad = cancel => _pad = connectPad(cancel),
                FindGame = _ => new FakeGame(),
                CreateForeground = flag => Foreground = new FakeForeground(flag),
                CreatePoller = (_, snapshot, config) => new SensorPoller(openSensor, snapshot, config),
            });
        }

        public MappingSession Session { get; }

        public FakeForeground? Foreground { get; private set; }

        public VirtualPad? Pad => _pad;

        public void Dispose()
        {
            Session.Dispose();
            _keyboards.Dispose();
        }
    }

    /// <summary>The real driver, except that an engine-thread submit, once armed, lands and then never returns until released.</summary>
    private sealed class WedgingDriver(IPadDriver inner, ManualResetEventSlim release) : IPadDriver
    {
        private int _armed;
        private int _engineCalls;
        private long _wedgedAt;

        public ManualResetEventSlim Wedged { get; } = new(false);

        public long WedgedAtTicks => Volatile.Read(ref _wedgedAt);

        /// <summary>Driver calls from the engine thread after the wedge.</summary>
        public int EngineCalls => Volatile.Read(ref _engineCalls);

        public int UserIndex => inner.UserIndex;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Connect() => inner.Connect();

        public void Submit(in PadReport report)
        {
            var engine = Thread.CurrentThread.Name == "apex-engine";
            if (engine && Wedged.IsSet)
            {
                Interlocked.Increment(ref _engineCalls);
            }
            inner.Submit(report);
            if (engine && Volatile.Read(ref _armed) == 1 && !Wedged.IsSet)
            {
                Volatile.Write(ref _wedgedAt, Stopwatch.GetTimestamp());
                Wedged.Set();
                release.Wait();
            }
        }

        public void Disconnect() => inner.Disconnect();

        public bool TryReadBack(int userIndex, out PadReport report, out uint packetNumber) => inner.TryReadBack(userIndex, out report, out packetNumber);

        public void Dispose()
        {
            inner.Dispose();
            Wedged.Dispose();
        }
    }

    /// <summary>Publishes groups 2 and 3 every millisecond with one key sweeping its travel, as a poller would.</summary>
    private sealed class SweepPublisher(SensorSnapshot shared, ScanCode key, int periodMs) : IDisposable
    {
        private int _stop;
        private Thread? _running;

        public void Start()
        {
            _running = new Thread(Run) { IsBackground = true, Name = "sweep", Priority = ThreadPriority.AboveNormal };
            _running.Start();
        }

        public void Stop()
        {
            Volatile.Write(ref _stop, 1);
            _running?.Join();
        }

        private void Run()
        {
            var cal = Calibrations()[key];
            var group = SensorMap.GroupOf(cal.SensorIndex);
            var raw2 = RestGroup(2);
            var raw3 = RestGroup(3);
            var swept = group == 2 ? raw2 : raw3;
            var filtered = new ushort[SensorProtocol.SensorsPerGroup];
            var working = new SensorSnapshot();
            using var timer = new HighResolutionTimer(1);
            for (var i = 0; Volatile.Read(ref _stop) == 0 && timer.WaitNext(); i++)
            {
                var phase = i % periodMs;
                var half = periodMs / 2;
                var depth = phase < half ? phase / (float)half : (periodMs - phase) / (float)half;
                swept[SensorMap.SlotOf(cal.SensorIndex)] = (ushort)(cal.Rest + cal.NoiseBand + depth * (Span - cal.NoiseBand));
                var now = Stopwatch.GetTimestamp();
                working.Begin(now);
                working.SetGroup(2, raw2, filtered);
                working.SetGroup(3, raw3, filtered);
                working.Stamp(now);
                shared.Publish(working);
            }
        }

        public void Dispose() => Stop();

        private static ushort[] RestGroup(int group)
        {
            var raw = new ushort[SensorProtocol.SensorsPerGroup];
            SensorProtocol.ParseGroup(Fixtures.RestGroup(group), raw, new ushort[SensorProtocol.SensorsPerGroup]);
            return raw;
        }
    }

    /// <summary>
    /// A vendor stream for the soak: the group 3 rest capture with one key sweeping its
    /// travel over a second, one reply every three milliseconds, allocation-free.
    /// </summary>
    private sealed class SweepStream(KeyCalibration swept) : IVendorStream
    {
        private readonly byte[] _group3 = Fixtures.RestGroup(3);
        private readonly byte[] _firmware = Fixtures.Firmware;
        private readonly int _offset = 1 + 2 * SensorMap.SlotOf(swept.SensorIndex);
        private readonly long _start = Stopwatch.GetTimestamp();
        private byte _command;
        private int _resting;

        /// <summary>From now on the key reads at rest.</summary>
        public void Rest() => Volatile.Write(ref _resting, 1);

        public void Write(byte[] buffer, int offset, int count) => _command = buffer[offset + 1];

        public int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(3);
            var reply = _command == SensorRequest.FirmwareCommand ? _firmware : _group3;
            if (_command == SensorRequest.GroupCommand)
            {
                var phase = (Stopwatch.GetTimestamp() - _start) % Stopwatch.Frequency / (double)Stopwatch.Frequency;
                var depth = phase < 0.5 ? phase * 2 : 2 - phase * 2;
                var value = Volatile.Read(ref _resting) == 1
                    ? swept.Rest
                    : (int)(swept.Rest + swept.NoiseBand + depth * (Span - swept.NoiseBand));
                _group3[_offset] = (byte)value;
                _group3[_offset + 1] = (byte)(value >> 8);
            }
            var length = Math.Min(reply.Length, count);
            Array.Copy(reply, 0, buffer, offset, length);
            return length;
        }

        public void Dispose()
        {
        }
    }
}