namespace ApexMapper.App.Services;

public interface IProfileManualPinStore
{
    string? Get();
    void Set(string? profileId);
}
