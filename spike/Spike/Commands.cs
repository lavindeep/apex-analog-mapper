using System.Diagnostics;
using System.Globalization;
using HidSharp;

namespace Spike;

internal static class Commands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // probe: enumerate, firmware, dump fixtures at rest (and with W held if asked).
    public static void Probe(bool withWHeld)
    {
        Console.WriteLine("SteelSeries HID interfaces:");
        foreach (var d in Vendor.SteelSeriesDevices())
        {
            string usages;
            int inLen, outLen;
            try
            {
                inLen = d.GetMaxInputReportLength();
                outLen = d.GetMaxOutputReportLength();
                usages = string.Join(" ", d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Distinct().Select(u => "0x" + u.ToString("X8", Inv)));
            }
            catch (Exception e)
            {
                inLen = outLen = -1;
                usages = "(descriptor unavailable: " + e.GetType().Name + ")";
            }
            Console.WriteLine($"  pid 0x{d.ProductID:X4} in={inLen} out={outLen} usages={usages} vendor={Vendor.IsVendorInterface(d)}");
            Console.WriteLine($"      {d.DevicePath}");
        }

        using var v = Vendor.Open();
        var fw = v.Firmware();
        Console.WriteLine($"Firmware: '{fw}'");
        var fixtures = Stats.FixturesDir();
        var reply = new byte[Vendor.ReportLength];
        v.Exchange(0x90, 0, reply);
        File.WriteAllText(Path.Combine(fixtures, "firmware.hex"), Convert.ToHexString(reply) + "\n");
        DumpGroups(v, fixtures, "rest");
        Console.WriteLine("Rest fixtures written.");
        if (withWHeld)
        {
            Console.WriteLine("Hold W fully down, then press Enter (on another key path, use the mouse to focus if needed)...");
            Console.ReadLine();
            DumpGroups(v, fixtures, "w-held");
            Console.WriteLine("W-held fixtures written. Release W.");
        }
    }

    private static void DumpGroups(Vendor v, string dir, string tag)
    {
        var reply = new byte[Vendor.ReportLength];
        for (byte g = 1; g <= 5; g++)
        {
            v.Drain();
            v.ReadGroup(g, reply);
            File.WriteAllText(Path.Combine(dir, $"{tag}-group{g}.hex"), Convert.ToHexString(reply) + "\n");
            var raw = string.Join(" ", Enumerable.Range(0, 14).Select(s => Vendor.Raw(reply, s)));
            var flt = string.Join(" ", Enumerable.Range(0, 14).Select(s => Vendor.Filtered(reply, s)));
            Console.WriteLine($"  {tag} group {g} raw: {raw}");
            Console.WriteLine($"  {tag} group {g} flt: {flt}");
        }
    }

    // cycle: back-to-back group 2 and 3 polls; exchange latency and cycle period.
    public static void Cycle(int seconds, bool drain)
    {
        Console.WriteLine($"SteelSeries processes running: {Stats.Processes("SteelSeries")}");
        using var v = Vendor.Open();
        Console.WriteLine($"Firmware {v.Firmware()}, drain-before-write={drain}");
        var reply = new byte[Vendor.ReportLength];
        var exchange = new List<double>();
        var cycle = new List<double>();
        var drained = 0;
        using var csv = Stats.Csv($"cycle-{(drain ? "drain" : "nodrain")}.csv", "t_ms,group,exchange_ms");
        var start = Stopwatch.GetTimestamp();
        var lastCycle = start;
        var faults = 0;
        while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
        {
            try
            {
                foreach (var g in new byte[] { 2, 3 })
                {
                    if (drain)
                    {
                        drained += v.Drain();
                    }
                    var ticks = v.ReadGroup(g, reply);
                    var ms = Stats.TicksToMs(ticks);
                    exchange.Add(ms);
                    csv.WriteLine(string.Create(Inv, $"{Stats.TicksToMs(Stopwatch.GetTimestamp() - start):F3},{g},{ms:F3}"));
                }
                var now = Stopwatch.GetTimestamp();
                cycle.Add(Stats.TicksToMs(now - lastCycle));
                lastCycle = now;
            }
            catch (Exception e)
            {
                faults++;
                Console.WriteLine($"fault: {e.Message}");
                Thread.Sleep(100);
                lastCycle = Stopwatch.GetTimestamp();
            }
        }
        Console.WriteLine(Stats.Summary("exchange", exchange));
        Console.WriteLine(Stats.Summary("cycle (2 groups)", cycle));
        Console.WriteLine($"faults={faults} drainedStaleReplies={drained}");
    }

    // pipeline: does the firmware queue requests? Write group 2 and 3 back to back,
    // then read two replies. Group 3 is recognisable by its empty slot 12 (< 50).
    public static void Pipeline(int seconds)
    {
        using var v = Vendor.Open();
        Console.WriteLine($"Firmware {v.Firmware()}");
        var r1 = new byte[Vendor.ReportLength];
        var r2 = new byte[Vendor.ReportLength];
        var cycle = new List<double>();
        var ordered = 0;
        var swapped = 0;
        var bad = 0;
        var start = Stopwatch.GetTimestamp();
        while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                v.WriteRequest(0xD7, 2);
                v.WriteRequest(0xD7, 3);
                v.ReadReply(r1);
                v.ReadReply(r2);
                var first3 = Vendor.Raw(r1, 12) < 50;
                var second3 = Vendor.Raw(r2, 12) < 50;
                if (!first3 && second3)
                {
                    ordered++;
                }
                else if (first3 && !second3)
                {
                    swapped++;
                }
                else
                {
                    bad++;
                }
                cycle.Add(Stats.TicksToMs(Stopwatch.GetTimestamp() - t0));
            }
            catch (Exception e)
            {
                bad++;
                Console.WriteLine($"fault: {e.Message}");
                Thread.Sleep(100);
            }
        }
        Console.WriteLine(Stats.Summary("pipelined cycle (2 groups)", cycle));
        Console.WriteLine($"ordered={ordered} swapped={swapped} bad={bad}");
    }

    // step: group 2 at max rate while the user taps W; raw vs filtered response.
    public static void Step(int seconds)
    {
        using var v = Vendor.Open();
        Console.WriteLine($"Firmware {v.Firmware()}. Tap W repeatedly, fast and fully, for {seconds} s. Starting in 2 s...");
        Thread.Sleep(2000);
        var reply = new byte[Vendor.ReportLength];
        var t = new List<double>();
        var raw = new List<int>();
        var flt = new List<int>();
        var start = Stopwatch.GetTimestamp();
        using (var csv = Stats.Csv("step.csv", "t_ms,raw,filtered"))
        {
            while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
            {
                v.ReadGroup(2, reply);
                var ms = Stats.TicksToMs(Stopwatch.GetTimestamp() - start);
                t.Add(ms);
                raw.Add(Vendor.Raw(reply, 2));
                flt.Add(Vendor.Filtered(reply, 2));
                csv.WriteLine(string.Create(Inv, $"{ms:F3},{raw[^1]},{flt[^1]}"));
            }
        }
        AnalyzeStep(t, raw, flt);
    }

    private static void AnalyzeStep(List<double> t, List<int> raw, List<int> flt)
    {
        var periods = new List<double>();
        for (var i = 1; i < t.Count; i++)
        {
            periods.Add(t[i] - t[i - 1]);
        }
        Console.WriteLine(Stats.Summary("sample period", periods));
        // Rest is the floor of the signal (travel reads upward), so take the median of
        // the lowest 30% rather than the first samples, which may include taps.
        var restRaw = Floor(raw);
        var restFlt = Floor(flt);
        Console.WriteLine($"rest raw={restRaw} filtered={restFlt}");

        // Presses: |raw - rest| rises above 100 counts, later falls below 30.
        var riseRaw = new List<double>();
        var riseFlt = new List<double>();
        var lag50 = new List<double>();
        var peaks = new List<int>();
        var i0 = 0;
        while (i0 < raw.Count)
        {
            var startIdx = -1;
            for (var i = i0; i < raw.Count; i++)
            {
                if (Math.Abs(raw[i] - restRaw) > 100)
                {
                    startIdx = i;
                    break;
                }
            }
            if (startIdx < 0)
            {
                break;
            }
            var endIdx = raw.Count - 1;
            for (var i = startIdx; i < raw.Count; i++)
            {
                if (Math.Abs(raw[i] - restRaw) < 30)
                {
                    endIdx = i;
                    break;
                }
            }
            // Walk back to where the rise began.
            var from = startIdx;
            while (from > i0 && Math.Abs(raw[from - 1] - restRaw) > 10)
            {
                from--;
            }
            var peak = 0;
            var peakIdx = from;
            for (var i = from; i <= endIdx; i++)
            {
                var d = Math.Abs(raw[i] - restRaw);
                if (d > peak)
                {
                    peak = d;
                    peakIdx = i;
                }
            }
            if (peak > 300)
            {
                peaks.Add(peak);
                var r10 = Cross(t, raw, restRaw, from, peakIdx, 0.10 * peak);
                var r90 = Cross(t, raw, restRaw, from, peakIdx, 0.90 * peak);
                var f10 = Cross(t, flt, restFlt, from, Math.Min(peakIdx + 20, t.Count - 1), 0.10 * peak);
                var f90 = Cross(t, flt, restFlt, from, Math.Min(peakIdx + 20, t.Count - 1), 0.90 * peak);
                var r50 = Cross(t, raw, restRaw, from, peakIdx, 0.50 * peak);
                var f50 = Cross(t, flt, restFlt, from, Math.Min(peakIdx + 20, t.Count - 1), 0.50 * peak);
                if (!double.IsNaN(r10) && !double.IsNaN(r90))
                {
                    riseRaw.Add(r90 - r10);
                }
                if (!double.IsNaN(f10) && !double.IsNaN(f90))
                {
                    riseFlt.Add(f90 - f10);
                }
                if (!double.IsNaN(r50) && !double.IsNaN(f50))
                {
                    lag50.Add(f50 - r50);
                }
            }
            i0 = endIdx + 1;
        }
        Console.WriteLine($"presses detected: {peaks.Count}, peak counts: {(peaks.Count > 0 ? Median(peaks) : 0)} median");
        Console.WriteLine(Stats.Summary("raw 10-90 rise", riseRaw));
        Console.WriteLine(Stats.Summary("filtered 10-90 rise", riseFlt));
        Console.WriteLine(Stats.Summary("filtered lag behind raw at 50%", lag50));
    }

    private static double Cross(List<double> t, List<int> s, int rest, int from, int to, double level)
    {
        for (var i = from; i <= to && i < s.Count; i++)
        {
            if (Math.Abs(s[i] - rest) >= level)
            {
                return t[i];
            }
        }
        return double.NaN;
    }

    // The 15th percentile: the middle of the lowest 30%, which is the rest plateau
    // when travel reads upward and taps are short.
    private static int Floor(List<int> xs)
    {
        var l = xs.ToList();
        l.Sort();
        var n = Math.Max(1, l.Count * 3 / 10);
        return l[n / 2];
    }

    private static int Median(IEnumerable<int> xs)
    {
        var l = xs.ToList();
        l.Sort();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }

    // tick: three ways to wait 1 ms.
    public static void Tick(int seconds)
    {
        Run("Thread.Sleep(1)", seconds, () => Thread.Sleep(1));
        using (var ev = new ManualResetEvent(false))
        {
            Run("WaitHandle.WaitOne(1)", seconds, () => ev.WaitOne(1));
        }
        using (var timer = new HighResTimer(1))
        {
            Run("high-resolution waitable timer", seconds, timer.WaitNext);
        }
        Native.timeBeginPeriod(1);
        try
        {
            Run("Thread.Sleep(1) with timeBeginPeriod(1)", seconds, () => Thread.Sleep(1));
        }
        finally
        {
            Native.timeEndPeriod(1);
        }

        static void Run(string name, int seconds, Action wait)
        {
            var periods = new List<double>(seconds * 1200);
            var start = Stopwatch.GetTimestamp();
            var last = start;
            while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
            {
                wait();
                var now = Stopwatch.GetTimestamp();
                periods.Add(Stats.TicksToMs(now - last));
                last = now;
            }
            Console.WriteLine(Stats.Summary(name, periods));
        }
    }

    // readback: submit-to-XInput latency floor with no keyboard involved.
    public static void Readback(int seconds)
    {
        using var pad = new Pad();
        Console.WriteLine($"Pad on XInput slot {pad.Slot}");
        var submitTicks = new long[32768];
        var latencies = new List<double>();
        var samplerPeriods = new List<double>();
        var packets = 0;
        var stop = false;
        var sampler = new Thread(() =>
        {
            using var timer = new HighResTimer(1);
            var last = Stopwatch.GetTimestamp();
            short lastX = 0;
            uint lastPacket = 0;
            while (!Volatile.Read(ref stop))
            {
                timer.WaitNext();
                var now = Stopwatch.GetTimestamp();
                samplerPeriods.Add(Stats.TicksToMs(now - last));
                last = now;
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
                            latencies.Add(Stats.TicksToMs(now - submitted));
                        }
                        lastX = s.Gamepad.sThumbLX;
                    }
                }
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
        Console.WriteLine(Stats.Summary("submit-to-readback", latencies));
        Console.WriteLine(Stats.Summary("sampler period", samplerPeriods));
        Console.WriteLine($"distinct XInput packets per second: {packets / (double)seconds:F0} (submits at 500/s)");
    }

    // e2e: W depth to right trigger; sensor-crossing and hook anchors vs XInput readback.
    public static void E2E(int seconds)
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
        var rest = Median(restSamples);
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
        if (Math.Abs(span) < 200)
        {
            Console.WriteLine("Span too small; press W fully next time.");
            return;
        }
        Thread.Sleep(1500);

        using var pad = new Pad();
        using var hook = new KeyHook(swallowW: false);
        long crossTicks = 0;
        var stop = false;
        var sensorToRead = new List<double>();
        var hookToRead = new List<double>();
        // The sampler fires at the same 5% depth the sensor anchor uses (13 of 255),
        // so a readback can never precede its own anchor.
        const byte samplerLevel = 13;
        var sampler = new Thread(() =>
        {
            using var timer = new HighResTimer(1);
            var wasZero = true;
            while (!Volatile.Read(ref stop))
            {
                timer.WaitNext();
                if (Native.XInputGetState((uint)pad.Slot, out var s) != 0)
                {
                    continue;
                }
                var rt = s.Gamepad.bRightTrigger;
                if (wasZero && rt >= samplerLevel)
                {
                    var now = Stopwatch.GetTimestamp();
                    var c = Volatile.Read(ref crossTicks);
                    var h = Volatile.Read(ref hook.LastWDownTicks);
                    if (c != 0)
                    {
                        sensorToRead.Add(Stats.TicksToMs(now - c));
                    }
                    if (h != 0)
                    {
                        hookToRead.Add(Stats.TicksToMs(now - h));
                    }
                }
                wasZero = rt < samplerLevel / 2;
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();

        var start = Stopwatch.GetTimestamp();
        var threshold = 0.05 * Math.Abs(span);
        var below = true;
        byte lastRt = 0;
        while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < seconds * 1000)
        {
            v.ReadGroup(2, reply);
            var now = Stopwatch.GetTimestamp();
            var depth = Math.Clamp((Vendor.Raw(reply, 2) - rest) / (double)span, 0, 1);
            var d = Math.Abs(Vendor.Raw(reply, 2) - rest);
            if (below && d > threshold)
            {
                Volatile.Write(ref crossTicks, now);
                below = false;
            }
            else if (!below && d < threshold * 0.5)
            {
                below = true;
            }
            var rt = (byte)Math.Round(depth * 255);
            if (rt != lastRt)
            {
                pad.SetRightTrigger(rt);
                lastRt = rt;
            }
        }
        Volatile.Write(ref stop, true);
        sampler.Join();
        Console.WriteLine($"W presses seen by hook: {hook.WDownCount}");
        Console.WriteLine(Stats.Summary("HEADLINE sensor-crossing to XInput readback", sensorToRead));
        Console.WriteLine(Stats.Summary("digital actuation offset: hook key-down to XInput readback", hookToRead));
    }

    // rest: noise and drift over minutes, all 28 sensors of groups 2 and 3.
    public static void Rest(int minutes)
    {
        using var v = Vendor.Open();
        Console.WriteLine($"Firmware {v.Firmware()}. Keep all keys released for {minutes} min.");
        var reply = new byte[Vendor.ReportLength];
        var raw = new List<int>[28];
        var flt = new List<int>[28];
        for (var i = 0; i < 28; i++)
        {
            raw[i] = new List<int>();
            flt[i] = new List<int>();
        }
        var start = Stopwatch.GetTimestamp();
        var samples = 0;
        var faults = 0;
        while (Stats.TicksToMs(Stopwatch.GetTimestamp() - start) < minutes * 60_000)
        {
            try
            {
                foreach (var g in new byte[] { 2, 3 })
                {
                    v.ReadGroup(g, reply);
                    for (var s = 0; s < 14; s++)
                    {
                        raw[(g - 2) * 14 + s].Add(Vendor.Raw(reply, s));
                        flt[(g - 2) * 14 + s].Add(Vendor.Filtered(reply, s));
                    }
                }
                samples++;
            }
            catch (Exception e)
            {
                faults++;
                Console.WriteLine($"fault: {e.Message}");
                Thread.Sleep(200);
            }
        }
        Console.WriteLine($"samples={samples} faults={faults}");
        Console.WriteLine("key  raw:mean p-p min max drift | filtered:mean p-p drift");
        foreach (var (key, group, slot) in Vendor.Wasd)
        {
            var i = (group - 2) * 14 + slot;
            Console.WriteLine($"{key}    {Describe(raw[i])} | {Describe(flt[i])}");
        }
        var worstRaw = Enumerable.Range(0, 28).Max(i => raw[i].Count == 0 ? 0 : raw[i].Max() - raw[i].Min());
        var worstFlt = Enumerable.Range(0, 28).Max(i => flt[i].Count == 0 ? 0 : flt[i].Max() - flt[i].Min());
        Console.WriteLine($"worst p-p over all 28 sensors: raw={worstRaw} filtered={worstFlt}");

        static string Describe(List<int> s)
        {
            if (s.Count < 10)
            {
                return "n/a";
            }
            var n = Math.Max(1, s.Count / 100);
            var first = s.Take(n * 5).Average();
            var last = s.Skip(s.Count - n * 5).Average();
            return string.Create(Inv, $"{s.Average():F1} {s.Max() - s.Min()} {s.Min()} {s.Max()} {last - first:+0.0;-0.0}");
        }
    }

    // travel: rest, full press, span, and the count at which the digital key fires.
    public static void Travel()
    {
        using var v = Vendor.Open();
        Console.WriteLine($"Firmware {v.Firmware()}");
        var reply = new byte[Vendor.ReportLength];
        using var hook = new KeyHook(swallowW: false);
        foreach (var (key, group, slot) in Vendor.Wasd)
        {
            Console.WriteLine($"[{key}] Keep it released. Sampling rest...");
            Thread.Sleep(1000);
            var rest = Median(Enumerable.Range(0, 100).Select(_ => { v.ReadGroup(group, reply); return (int)Vendor.Raw(reply, slot); }));
            Console.WriteLine($"[{key}] rest={rest}. Now press {key} SLOWLY all the way down over about 3 seconds, then release.");
            var full = rest;
            var digitalAt = -1;
            var lastDown = hook.WDownCount;
            var sw = Stopwatch.StartNew();
            var pressed = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                v.ReadGroup(group, reply);
                var r = Vendor.Raw(reply, slot);
                if (Math.Abs(r - rest) > Math.Abs(full - rest))
                {
                    full = r;
                }
                if (Math.Abs(r - rest) > 100)
                {
                    pressed = true;
                }
                if (key == "W" && digitalAt < 0 && hook.WDownCount > lastDown)
                {
                    digitalAt = r;
                }
                if (pressed && Math.Abs(r - rest) < 30 && sw.Elapsed > TimeSpan.FromSeconds(2))
                {
                    break;
                }
            }
            var span = Math.Abs(full - rest);
            var digitalPct = digitalAt < 0 || span == 0 ? double.NaN : 100.0 * Math.Abs(digitalAt - rest) / span;
            Console.WriteLine($"[{key}] rest={rest} full={full} span={span} direction={(full > rest ? "up" : "down")} digitalFiresAt={(digitalAt < 0 ? "n/a (hook tracks W only)" : digitalAt + $" ({digitalPct:F0}% of travel)")}");
        }
    }

    // kill: does TerminateProcess unplug the pad and remove the hook?
    public static int Kill(string self)
    {
        // Parent's observer hook goes in first so the child's newer hook runs ahead of it.
        using var observer = new KeyHook(swallowW: false);
        var psi = new ProcessStartInfo(self, "--child")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var child = Process.Start(psi)!;
        var slotLine = child.StandardOutput.ReadLine();
        if (slotLine is null || !slotLine.StartsWith("slot ", StringComparison.Ordinal))
        {
            Console.WriteLine($"child did not report a slot: '{slotLine}'");
            child.Kill();
            return 1;
        }
        var slot = uint.Parse(slotLine[5..], Inv);
        Thread.Sleep(500);
        var rc = Native.XInputGetState(slot, out var before);
        Console.WriteLine($"before kill: XInputGetState={rc} RT={before.Gamepad.bRightTrigger}");
        var seenBefore = observer.WSeenCount;
        Native.SendTestKey(true);
        Native.SendTestKey(false);
        Thread.Sleep(200);
        var passedBefore = observer.WSeenCount - seenBefore;
        Console.WriteLine($"synthetic W events reaching the parent observer while child hook alive: {passedBefore} (expect 0)");

        var t0 = Stopwatch.GetTimestamp();
        child.Kill();
        child.WaitForExit();
        double unplugMs = double.NaN;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            rc = Native.XInputGetState(slot, out var s);
            if (rc == Native.ERROR_DEVICE_NOT_CONNECTED)
            {
                unplugMs = Stats.TicksToMs(Stopwatch.GetTimestamp() - t0);
                break;
            }
            Thread.Sleep(5);
        }
        Console.WriteLine(double.IsNaN(unplugMs)
            ? "PAD LINGERED: still connected 5 s after TerminateProcess"
            : $"pad unplugged {unplugMs:F0} ms after TerminateProcess");
        Thread.Sleep(200);
        var seenAfter = observer.WSeenCount;
        Native.SendTestKey(true);
        Native.SendTestKey(false);
        Thread.Sleep(200);
        var passedAfter = observer.WSeenCount - seenAfter;
        Console.WriteLine($"synthetic W events reaching the parent observer after kill: {passedAfter} (expect 2)");
        return double.IsNaN(unplugMs) || passedBefore != 0 || passedAfter != 2 ? 1 : 0;
    }

    public static void Child()
    {
        var pad = new Pad();
        pad.SetRightTrigger(255);
        var hook = new KeyHook(swallowW: true);
        Console.Out.WriteLine($"slot {pad.Slot}");
        Console.Out.Flush();
        // Block the main thread forever; the hook thread keeps pumping.
        Thread.Sleep(Timeout.Infinite);
        GC.KeepAlive(hook);
        GC.KeepAlive(pad);
    }
}
