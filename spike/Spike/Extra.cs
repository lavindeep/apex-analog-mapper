using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HidSharp;

namespace Spike;

// Follow-up measurements requested by the stage 0 review.
internal static unsafe class Extra
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // containers: container id per SteelSeries HID interface via cfgmgr32.
    public static void Containers()
    {
        foreach (var d in Vendor.SteelSeriesDevices())
        {
            var container = ContainerId(d.DevicePath);
            Console.WriteLine($"  pid 0x{d.ProductID:X4} in={SafeLen(() => d.GetMaxInputReportLength())} container={container}");
            Console.WriteLine($"      {d.DevicePath}");
        }

        static string SafeLen(Func<int> f)
        {
            try
            {
                return f().ToString(Inv);
            }
            catch
            {
                return "?";
            }
        }
    }

    private static readonly Guid DevpkeyDeviceContainerIdFmt = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
    private static readonly Guid DevpkeyDeviceInstanceIdFmt = new("78c34fc8-104a-4aca-9ea4-524d52996e57");

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, ref DEVPROPKEY key, out uint type, byte* buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte* buffer, ref uint size, uint flags);

    public static string ContainerId(string interfacePath)
    {
        try
        {
            var instanceKey = new DEVPROPKEY { fmtid = DevpkeyDeviceInstanceIdFmt, pid = 256 };
            var buffer = stackalloc byte[1024];
            uint size = 1024;
            var rc = CM_Get_Device_Interface_PropertyW(interfacePath, ref instanceKey, out _, buffer, ref size, 0);
            if (rc != 0)
            {
                return $"(instance id error {rc})";
            }
            var instanceId = Marshal.PtrToStringUni((nint)buffer)!;
            rc = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
            if (rc != 0)
            {
                return $"(locate error {rc})";
            }
            var containerKey = new DEVPROPKEY { fmtid = DevpkeyDeviceContainerIdFmt, pid = 2 };
            size = 1024;
            rc = CM_Get_DevNode_PropertyW(devInst, ref containerKey, out _, buffer, ref size, 0);
            if (rc != 0)
            {
                return $"(container error {rc})";
            }
            return new Guid(new ReadOnlySpan<byte>(buffer, 16)).ToString();
        }
        catch (Exception e)
        {
            return "(" + e.GetType().Name + ")";
        }
    }

    // listen: does the 0xFFC1 interface (mi_04) stream anything unsolicited?
    public static void Listen(int seconds)
    {
        var device = Vendor.SteelSeriesDevices().FirstOrDefault(d =>
        {
            try
            {
                return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(0xFFC10001u));
            }
            catch
            {
                return false;
            }
        });
        if (device is null)
        {
            Console.WriteLine("no 0xFFC1 interface found");
            return;
        }
        Console.WriteLine($"opening {device.DevicePath} in={device.GetMaxInputReportLength()} out={device.GetMaxOutputReportLength()}");
        if (!device.TryOpen(out var stream))
        {
            Console.WriteLine("TryOpen failed");
            return;
        }
        using (stream)
        {
            stream.ReadTimeout = 500;
            var buffer = new byte[Math.Max(65, device.GetMaxInputReportLength())];
            var reports = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                try
                {
                    var n = stream.Read(buffer, 0, buffer.Length);
                    if (n > 0)
                    {
                        reports++;
                        if (reports <= 5)
                        {
                            Console.WriteLine($"  t={sw.Elapsed.TotalMilliseconds:F0}ms {n} bytes: {Convert.ToHexString(buffer, 0, n)}");
                        }
                    }
                }
                catch (TimeoutException)
                {
                }
            }
            Console.WriteLine($"reports in {seconds} s: {reports}");
        }
    }

    // hookcost: callback duration under synthetic presses, observer and swallowing.
    public static void HookCost(int presses)
    {
        foreach (var swallow in new[] { false, true })
        {
            using var hook = new TimedHook(swallow);
            for (var i = 0; i < presses; i++)
            {
                Native.SendTestKey(true);
                Thread.Sleep(2);
                Native.SendTestKey(false);
                Thread.Sleep(2);
            }
            Thread.Sleep(200);
            var durations = hook.Durations();
            var sorted = durations.ToList();
            sorted.Sort();
            Console.WriteLine(Stats.Summary($"hook callback (swallow={swallow})", durations, "ms") + $" p99.9={Stats.Percentile(sorted, 0.999):F3} over1ms={sorted.Count(d => d > 1.0)}");
        }
    }

    private sealed class TimedHook : IDisposable
    {
        private readonly Native.HookProc _proc;
        private readonly bool _swallow;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly double[] _ring = new double[4096];
        private int _count;
        private nint _hook;
        private uint _threadId;

        public TimedHook(bool swallow)
        {
            _swallow = swallow;
            _proc = Callback;
            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            _ready.Wait();
        }

        private void Run()
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
            _ready.Set();
            while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            Native.UnhookWindowsHookEx(_hook);
        }

        private nint Callback(int code, nint wParam, nint lParam)
        {
            var t0 = Stopwatch.GetTimestamp();
            nint result = 0;
            if (code >= 0)
            {
                var k = (Native.KBDLLHOOKSTRUCT*)lParam;
                if (k->vkCode == Native.TestVk && _swallow)
                {
                    result = 1;
                }
            }
            if (result == 0)
            {
                result = Native.CallNextHookEx(_hook, code, wParam, lParam);
            }
            var i = Interlocked.Increment(ref _count) - 1;
            if (i < _ring.Length)
            {
                _ring[i] = Stats.TicksToMs(Stopwatch.GetTimestamp() - t0);
            }
            return result;
        }

        public IEnumerable<double> Durations() => _ring.Take(Math.Min(_count, _ring.Length));

        public void Dispose()
        {
            Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
            _thread.Join(2000);
        }
    }

    // disconnect: how long a graceful zero + disconnect takes from user mode.
    public static void Disconnect(int trials)
    {
        var times = new List<double>();
        for (var i = 0; i < trials; i++)
        {
            var pad = new Pad();
            pad.SetRightTrigger(255);
            Thread.Sleep(100);
            var t0 = Stopwatch.GetTimestamp();
            pad.Dispose();
            times.Add(Stats.TicksToMs(Stopwatch.GetTimestamp() - t0));
            Thread.Sleep(300);
        }
        Console.WriteLine(Stats.Summary("graceful zero + disconnect + client dispose", times));
    }

    // readback2: submit-to-readback with a spinning sampler (no timer quantisation).
    public static void Readback2(int seconds)
    {
        using var pad = new Pad();
        var submitTicks = new long[32768];
        var latencies = new List<double>();
        var stop = false;
        var packets = 0;
        var sampler = new Thread(() =>
        {
            short lastX = 0;
            uint lastPacket = 0;
            while (!Volatile.Read(ref stop))
            {
                if (Native.XInputGetState((uint)pad.Slot, out var s) == 0)
                {
                    if (s.dwPacketNumber != lastPacket)
                    {
                        packets++;
                        lastPacket = s.dwPacketNumber;
                    }
                    if (s.Gamepad.sThumbLX != lastX && s.Gamepad.sThumbLX > 0)
                    {
                        var submitted = Volatile.Read(ref submitTicks[s.Gamepad.sThumbLX]);
                        if (submitted != 0)
                        {
                            latencies.Add(Stats.TicksToMs(Stopwatch.GetTimestamp() - submitted));
                        }
                        lastX = s.Gamepad.sThumbLX;
                    }
                }
                Thread.SpinWait(50);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();
        using (var timer = new HighResTimer(2))
        {
            short x = 1;
            var start = Stopwatch.GetTimestamp();
            while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
            {
                timer.WaitNext();
                x = (short)(x % 32000 + 1);
                Volatile.Write(ref submitTicks[x], Stopwatch.GetTimestamp());
                pad.SetLeftX(x);
            }
        }
        Volatile.Write(ref stop, true);
        sampler.Join();
        Console.WriteLine(Stats.Summary("submit-to-readback (spin sampler)", latencies));
        Console.WriteLine($"distinct XInput packets per second: {packets / (double)seconds:F0} (submits at 500/s)");
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    // tickres: what timeBeginPeriod actually does on this PC.
    public static void TickRes()
    {
        NtQueryTimerResolution(out var min, out var max, out var cur);
        Console.WriteLine($"timer resolution before: min={min / 10000.0:F3}ms max={max / 10000.0:F3}ms current={cur / 10000.0:F3}ms");
        var rc = Native.timeBeginPeriod(1);
        NtQueryTimerResolution(out _, out _, out cur);
        Console.WriteLine($"timeBeginPeriod(1) returned {rc}; current={cur / 10000.0:F3}ms");
        var periods = new List<double>();
        var last = Stopwatch.GetTimestamp();
        for (var i = 0; i < 300; i++)
        {
            Thread.Sleep(1);
            var now = Stopwatch.GetTimestamp();
            periods.Add(Stats.TicksToMs(now - last));
            last = now;
        }
        Console.WriteLine(Stats.Summary("Thread.Sleep(1) with period 1 active", periods));
        Native.timeEndPeriod(1);
    }

    // pipeline2: per-read timestamps to see whether the firmware buffers requests.
    public static void Pipeline2(int seconds)
    {
        using var v = Vendor.Open();
        var r1 = new byte[Vendor.ReportLength];
        var r2 = new byte[Vendor.ReportLength];
        var first = new List<double>();
        var second = new List<double>();
        var start = Stopwatch.GetTimestamp();
        while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
        {
            var t0 = Stopwatch.GetTimestamp();
            v.WriteRequest(0xD7, 2);
            v.WriteRequest(0xD7, 3);
            v.ReadReply(r1);
            var t1 = Stopwatch.GetTimestamp();
            v.ReadReply(r2);
            var t2 = Stopwatch.GetTimestamp();
            first.Add(Stats.TicksToMs(t1 - t0));
            second.Add(Stats.TicksToMs(t2 - t1));
        }
        Console.WriteLine(Stats.Summary("both writes to first reply", first));
        Console.WriteLine(Stats.Summary("first reply to second reply", second));
    }

    // kill5: repeat the kill test.
    public static int KillRepeat(string self, int trials)
    {
        var failures = 0;
        for (var i = 0; i < trials; i++)
        {
            Console.WriteLine($"--- trial {i + 1} ---");
            failures += Commands.Kill(self);
            Thread.Sleep(500);
        }
        Console.WriteLine($"kill trials={trials} failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    // e2e2: both groups polled, consume-once anchors, medians only are meaningful.
    public static void E2E2(int seconds)
    {
        using var v = Vendor.Open();
        Console.WriteLine($"Firmware {v.Firmware()}. Keep W released for 1 s...");
        var reply = new byte[Vendor.ReportLength];
        var restSamples = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            v.ReadGroup(2, reply);
            restSamples.Add(Vendor.Raw(reply, 2));
        }
        restSamples.Sort();
        var rest = restSamples[restSamples.Count / 2];
        Console.WriteLine($"rest={rest}. Now press W fully once and hold until told...");
        var full = rest;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(4))
        {
            v.ReadGroup(2, reply);
            var r = Vendor.Raw(reply, 2);
            if (Math.Abs(r - rest) > Math.Abs(full - rest))
            {
                full = r;
            }
        }
        var span = full - rest;
        Console.WriteLine($"full={full} span={span}. Release W. Then tap W repeatedly for {seconds} s, with clear releases between taps.");
        Thread.Sleep(1500);

        using var pad = new Pad();
        using var hook = new KeyHook(swallowW: false);
        long crossTicks = 0;
        long hookTicks = 0;
        var stop = false;
        var sensorToRead = new List<double>();
        var hookToRead = new List<double>();
        var unpaired = 0;
        const byte samplerLevel = 13;
        var sampler = new Thread(() =>
        {
            var wasZero = true;
            while (!Volatile.Read(ref stop))
            {
                if (Native.XInputGetState((uint)pad.Slot, out var s) == 0)
                {
                    var rt = s.Gamepad.bRightTrigger;
                    if (wasZero && rt >= samplerLevel)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var c = Interlocked.Exchange(ref crossTicks, 0);
                        var h = Interlocked.Exchange(ref hookTicks, 0);
                        if (c != 0)
                        {
                            sensorToRead.Add(Stats.TicksToMs(now - c));
                        }
                        else
                        {
                            unpaired++;
                        }
                        if (h != 0)
                        {
                            hookToRead.Add(Stats.TicksToMs(now - h));
                        }
                    }
                    wasZero = rt < samplerLevel / 2;
                }
                Thread.SpinWait(50);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();

        var start = Stopwatch.GetTimestamp();
        var threshold = 0.05 * Math.Abs(span);
        var below = true;
        byte lastRt = 0;
        var lastHookCount = hook.WDownCount;
        while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
        {
            // The real product cycle: group 2 then group 3.
            v.ReadGroup(2, reply);
            var now = Stopwatch.GetTimestamp();
            var raw = Vendor.Raw(reply, 2);
            v.ReadGroup(3, reply);
            var d = Math.Abs(raw - rest);
            if (below && d > threshold)
            {
                Volatile.Write(ref crossTicks, now);
                below = false;
            }
            else if (!below && d < threshold * 0.5)
            {
                below = true;
            }
            if (hook.WDownCount != lastHookCount)
            {
                lastHookCount = hook.WDownCount;
                Volatile.Write(ref hookTicks, Volatile.Read(ref hook.LastWDownTicks));
            }
            var depth = Math.Clamp((raw - rest) / (double)span, 0, 1);
            var rt = (byte)Math.Round(depth * 255);
            if (rt != lastRt)
            {
                pad.SetRightTrigger(rt);
                lastRt = rt;
            }
        }
        Volatile.Write(ref stop, true);
        sampler.Join();
        Console.WriteLine($"W presses seen by hook: {hook.WDownCount}, unpaired readbacks: {unpaired}");
        Console.WriteLine(Stats.Summary("sensor read (5% crossing) to XInput readback, two-group cycle", sensorToRead));
        Console.WriteLine(Stats.Summary("keyboard digital key-down to XInput readback, two-group cycle", hookToRead));
    }
}
