using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;

// Child process for the kill hardware test, which has to end a process for real.
// Plugs in a pad at full right trigger and installs a hook that swallows W (injected
// presses included, as in test mode), prints "slot N" once both are in place, then
// blocks its main thread while the hook thread keeps pumping, until it is killed.

if (args is not ["kill-child"])
{
    Console.Error.WriteLine("Usage: ApexMapper.Harness kill-child");
    return 2;
}

var pad = VirtualPad.Connect(CancellationToken.None);
pad.TrySubmit(PadReport.Neutral with { RightTrigger = 255 }, 0);
var hook = new KeyboardHook(
    new KeyStateStore(),
    new HookPolicy { SwallowInjected = true },
    new ForegroundFlag { IsGameForeground = true },
    [new ScanCode(0x11)]);
hook.Start();
Console.Out.WriteLine($"slot {pad.UserIndex}");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);
GC.KeepAlive(hook);
GC.KeepAlive(pad);
return 0;
