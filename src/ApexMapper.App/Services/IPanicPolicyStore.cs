namespace ApexMapper.App.Services;

/// <summary>Stores the set of executables for which automatic panic-enable is suppressed.</summary>
public interface IPanicPolicyStore
{
    bool IsAutoEnableDisabled(string executablePath);
    void DisableAutoEnable(string executablePath);
    void EnableAutoEnable(string executablePath);
    IReadOnlyCollection<string> ListDisabled();
}
