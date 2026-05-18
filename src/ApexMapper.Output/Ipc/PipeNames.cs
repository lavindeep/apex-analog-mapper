namespace ApexMapper.Output.Ipc;

public static class PipeNames
{
    public static string ForSession(string sessionId) =>
        $@"ApexMapper.Supervisor.{sessionId}";
    public const string Prefix = @"\\.\pipe\";
}
