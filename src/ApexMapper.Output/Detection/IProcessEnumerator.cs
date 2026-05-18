namespace ApexMapper.Output.Detection;

public interface IProcessEnumerator
{
    IReadOnlyList<ProcessSnapshot> Enumerate();
    ProcessSnapshot? GetById(int processId);
}

public sealed record ProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
