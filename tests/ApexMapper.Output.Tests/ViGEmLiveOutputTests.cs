using System.Diagnostics;
using System.Runtime.InteropServices;
using ApexMapper.Core.Pipeline;
using ApexMapper.Output.ViGEm;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public sealed class ViGEmLiveOutputTests
{
    [LiveOutputFact]
    public void Connect_is_exactly_neutral_and_reports_round_trip_before_disconnect()
    {
        ConnectedSlots().Should().BeEmpty("live output checks require an idle desktop with no other XInput controllers");
        var output = new ViGEmXboxOutput();
        uint? slot = null;
        try
        {
            output.Connect();
            Await(() => ConnectedSlots().Length == 1, "the new controller should appear in XInput");
            slot = ConnectedSlots().Single();
            Await(() => IsNeutral(slot.Value), "Connect must publish exact zero, not the driver's startup stick offsets");

            output.Submit(new VirtualPadState
            {
                LeftStickX = 1f, LeftStickY = -1f, RightTrigger = 1f, ButtonA = true,
            });
            Await(() => XInputGetState(slot.Value, out var state) == 0
                && state.Gamepad.LeftX == 32767 && state.Gamepad.LeftY == -32767
                && state.Gamepad.RightTrigger == 255 && state.Gamepad.Buttons == 0x1000,
                "submitted axes, trigger and button should reach XInput");

            output.Zero();
            Await(() => IsNeutral(slot.Value), "Zero should clear all axes, triggers and buttons");
        }
        finally
        {
            output.Disconnect();
            if (slot is uint owned)
                Await(() => XInputGetState(owned, out _) == 1167, "disconnect should remove the owned controller");
        }
        output.IsConnected.Should().BeFalse();
    }

    private static uint[] ConnectedSlots() => Enumerable.Range(0, 4).Select(i => (uint)i)
        .Where(i => XInputGetState(i, out _) == 0).ToArray();

    private static bool IsNeutral(uint slot)
    {
        if (XInputGetState(slot, out var state) != 0) return false;
        var pad = state.Gamepad;
        return pad.Buttons == 0 && pad.LeftTrigger == 0 && pad.RightTrigger == 0
            && pad.LeftX == 0 && pad.LeftY == 0 && pad.RightX == 0 && pad.RightY == 0;
    }

    private static void Await(Func<bool> condition, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(5))
                throw new TimeoutException(reason);
            Thread.Sleep(25);
        }
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint index, out State state);

    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Packet; public Gamepad Gamepad; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }
}

public sealed class LiveOutputFactAttribute : FactAttribute
{
    public LiveOutputFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows native XInput and ViGEmBus.";
        else if (Environment.GetEnvironmentVariable("APEX_TEST_LIVE_OUTPUT") != "1")
            Skip = "Creates a controller and submits nonzero input. Set APEX_TEST_LIVE_OUTPUT=1 only on an idle desktop with ViGEmBus.";
    }
}
