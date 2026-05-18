namespace ApexMapper.App.Services;

public sealed record ProfileHotReloadOptions(
    string DirectoryPath,
    TimeSpan DebounceDelay)
{
    public ProfileHotReloadOptions(string directoryPath)
        : this(directoryPath, TimeSpan.FromMilliseconds(200)) { }
}
