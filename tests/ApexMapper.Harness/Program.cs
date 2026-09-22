using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;

// Child processes for tests that have to end a process for real.
//
// kill-child: plugs in a pad at full right trigger and installs a hook that swallows W
// (injected presses included, as in test mode), prints "slot N" once both are in place,
// then blocks its main thread while the hook thread keeps pumping, until it is killed.
//
// crash-child: installs a crash guard whose emergency prints "emergency ran", then
// throws on a raw thread, which ends the process through the unhandled-exception path.

if (args is ["crash-child"])
{
    using var guard = new CrashGuard(() =>
    {
        Console.Out.WriteLine("emergency ran");
        Console.Out.Flush();
    });
    var thread = new Thread(() => throw new InvalidOperationException("crash-child: a bug on a raw thread"));
    thread.Start();
    thread.Join();
    return 0;
}

if (args is not ["kill-child"])
{
    Console.Error.WriteLine("Usage: ApexMapper.Harness kill-child | crash-child");
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
