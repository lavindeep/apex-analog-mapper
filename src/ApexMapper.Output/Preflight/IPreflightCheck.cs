namespace ApexMapper.Output.Preflight;

public interface IPreflightCheck
{
    string CheckId { get; }
    PreflightIssue? Run();
}
