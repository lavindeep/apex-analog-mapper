using System.Diagnostics;
using System.Runtime.InteropServices;
using ApexMapper.Input.Abstractions.Pipeline;
using RawInputAdapter = ApexMapper.Input.RawInput.RawInputAdapter;

namespace ApexMapper.Input.Tests.RawInput;

[Trait("os", "windows")]
[Collection("RawInput")]
public class RawInputAdapterSmokeTests
{
    private const ushort SCAN_A = 0x1E;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [Fact]
    public async Task synthetic_keypress_arrives_in_ring()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(1024);
        await using var adapter = new RawInputAdapter(ring);
        await adapter.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(50);

            SendScanCodeKey(SCAN_A, keyUp: false);
            SendScanCodeKey(SCAN_A, keyUp: true);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            var gotDown = false;
            RawKeyEvent ev = default;
            while (DateTime.UtcNow < deadline)
            {
                while (ring.TryDequeue(out var dequeued))
                {
                    if (dequeued.IsDown && dequeued.ScanCode == SCAN_A)
                    {
                        ev = dequeued;
                        gotDown = true;
                        break;
                    }
                }

                if (gotDown)
                {
                    break;
                }

                await Task.Delay(10);
            }

            gotDown.Should().BeTrue();
            ev.ScanCode.Should().Be(SCAN_A);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("perf", "true")]
    public async Task latency_p95_under_8ms_over_1000_events()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(4096);
        await using var adapter = new RawInputAdapter(ring);
        await adapter.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(50);

            const int iterations = 1000;
            var samples = new List<long>(iterations);
            var ticksPerMs = Stopwatch.Frequency / 1000.0;

            for (var i = 0; i < iterations; i++)
            {
                var sent = Stopwatch.GetTimestamp();
                SendScanCodeKey(SCAN_A, keyUp: false);
                SendScanCodeKey(SCAN_A, keyUp: true);

                var deadline = sent + Stopwatch.Frequency;
                var found = false;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    if (ring.TryDequeue(out var ev))
                    {
                        if (ev.IsDown && ev.ScanCode == SCAN_A)
                        {
                            samples.Add(ev.TimestampTicks - sent);
                            found = true;
                            break;
                        }
                    }
                    else
                    {
                        Thread.SpinWait(50);
                    }
                }

                if (!found)
                {
                    break;
                }

                while (ring.TryDequeue(out _))
                {
                }
            }

            samples.Count.Should().BeGreaterOrEqualTo(900);
            samples.Sort();
            var p95Index = (int)(samples.Count * 0.95);
            var p95Ticks = samples[p95Index];
            var p95Ms = p95Ticks / ticksPerMs;
            p95Ms.Should().BeLessThan(8.0);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    private static void SendScanCodeKey(ushort scanCode, bool keyUp)
    {
        var flags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        var inputs = new INPUT[1];
        inputs[0].Type = INPUT_KEYBOARD;
        inputs[0].Union.Keyboard = new KEYBDINPUT
        {
            WVk = 0,
            WScan = scanCode,
            DwFlags = flags,
            Time = 0,
            DwExtraInfo = IntPtr.Zero,
        };

        var size = Marshal.SizeOf<INPUT>();
        var sent = SendInput(1, inputs, size);
        if (sent != 1)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput failed (Win32 {err}).");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }
}
