using System.Text.Json;
using ApexMapper.Persistence.Atomic;

namespace ApexMapper.App.Services;

/// <summary>
/// Persists the manually pinned profile id to a single JSON file.
/// Constructor accepts a directory path so tests can target temp dirs.
/// Set(null) clears the file.
/// </summary>
public sealed class JsonProfileManualPinStore : IProfileManualPinStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _filePath;

    public JsonProfileManualPinStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _filePath = Path.Combine(directoryPath, "profile-pin.json");
    }

    public string? Get()
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            var text = File.ReadAllText(_filePath);
            var doc = JsonSerializer.Deserialize<PinDocument>(text, JsonOptions);
            return doc?.ProfileId;
        }
        catch
        {
            return null;
        }
    }

    public void Set(string? profileId)
    {
        if (profileId is null)
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(new PinDocument(profileId), JsonOptions);
        AtomicFile.WriteAllText(_filePath, json);
    }

    private sealed record PinDocument(string ProfileId);
}
