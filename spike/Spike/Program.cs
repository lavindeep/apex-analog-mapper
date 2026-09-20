using System.Globalization;
using Spike;

// Measurement spike. See docs/design/2026-09-19-plan.md, stage 0c.
// Usage: Spike <command> [--seconds N] [--minutes N] [--no-drain] [--w-held]
// Console output is also written to spike/out/<command>.log.

var command = args.Length > 0 ? args[0] : "help";
int Arg(string name, int dflt)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : dflt;
}
bool Flag(string name) => Array.IndexOf(args, name) >= 0;

if (command != "--child")
{
    Console.SetOut(new Tee(Console.Out, new StreamWriter(Path.Combine(Stats.OutDir(), command + ".log"), false) { AutoFlush = true }));
}

try
{
    switch (command)
    {
        case "probe": Commands.Probe(Flag("--w-held")); break;
        case "cycle": Commands.Cycle(Arg("--seconds", 30), !Flag("--no-drain")); break;
        case "pipeline": Commands.Pipeline(Arg("--seconds", 20)); break;
        case "step": Commands.Step(Arg("--seconds", 20)); break;
        case "tick": Commands.Tick(Arg("--seconds", 10)); break;
        case "readback": Commands.Readback(Arg("--seconds", 10)); break;
        case "e2e": Commands.E2E(Arg("--seconds", 30)); break;
        case "rest": Commands.Rest(Arg("--minutes", 10)); break;
        case "travel": Commands.Travel(); break;
        case "kill": return Commands.Kill(Environment.ProcessPath!);
        case "--child": Commands.Child(); break;
        default:
            Console.WriteLine("commands: probe [--w-held] | cycle [--seconds N] [--no-drain] | pipeline | step | tick | readback | e2e | rest [--minutes N] | travel | kill");
            break;
    }
    return 0;
}
catch (Exception e)
{
    Console.WriteLine(e);
    return 2;
}

// Writes to the console and a log file at once.
internal sealed class Tee(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void Write(string? value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    public override void Flush() { a.Flush(); b.Flush(); }
}
