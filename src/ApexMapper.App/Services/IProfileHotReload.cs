namespace ApexMapper.App.Services;

/// <summary>Watches the profiles directory for changes and reloads profiles automatically.</summary>
public interface IProfileHotReload : IDisposable
{
    event EventHandler<ProfilesReloadedEventArgs>? ProfilesReloaded;

    void Start();
    void Stop();
}

public sealed class ProfilesReloadedEventArgs(
    IReadOnlyList<ApexMapper.Core.Engine.Profile> profiles) : EventArgs
{
    public IReadOnlyList<ApexMapper.Core.Engine.Profile> Profiles { get; } = profiles;
}
