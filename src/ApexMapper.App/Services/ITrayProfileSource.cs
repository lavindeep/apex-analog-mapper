using ApexMapper.App.ViewModels.Tray;

namespace ApexMapper.App.Services;

/// <summary>
/// Narrowly-scoped abstraction for the tray menu to read and switch profiles.
/// Streams 4.D/4.E will provide the concrete implementation during integrate.
/// </summary>
public interface ITrayProfileSource
{
    string CurrentProfileId { get; }
    IReadOnlyList<TrayProfileEntry> ListProfiles();
    void Switch(string profileId);
    event EventHandler? ProfilesChanged;
}
