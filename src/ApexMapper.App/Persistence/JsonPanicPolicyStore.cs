using System.Text.Json;
using ApexMapper.App.Services;
using ApexMapper.Persistence.Atomic;

namespace ApexMapper.App.Persistence;

/// <summary>
/// Persists the set of executables for which automatic panic-enable is suppressed.
/// The backing file is %AppData%/ApexMapper/panic-policy.json (or any directory
/// supplied via <see cref="PanicPolicyOptions"/> for testability).
/// Each mutating operation reads the file fresh, mutates, and atomically writes —
/// this is sufficient for the single-writer guarantee required by the done criteria.
/// </summary>
public sealed class JsonPanicPolicyStore : IPanicPolicyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _filePath;

    public JsonPanicPolicyStore(PanicPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _filePath = Path.Combine(options.DirectoryPath, "panic-policy.json");
    }

    public bool IsAutoEnableDisabled(string executablePath)
    {
        var set = LoadFromDisk();
        return set.Contains(Normalise(executablePath));
    }

    public void DisableAutoEnable(string executablePath)
    {
        var set = LoadFromDisk();
        set.Add(Normalise(executablePath));
        SaveToDisk(set);
    }

    public void EnableAutoEnable(string executablePath)
    {
        var set = LoadFromDisk();
        set.Remove(Normalise(executablePath));
        SaveToDisk(set);
    }

    public IReadOnlyCollection<string> ListDisabled()
        => LoadFromDisk();

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private HashSet<string> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var text = File.ReadAllText(_filePath);
            var paths = JsonSerializer.Deserialize<string[]>(text, JsonOptions);
            if (paths is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    set.Add(Normalise(p));
            }
            return set;
        }
        catch
        {
            // Corrupt or unreadable file — start fresh rather than crashing.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveToDisk(HashSet<string> set)
    {
        var array = set.ToArray();
        var json = JsonSerializer.Serialize(array, JsonOptions);
        AtomicFile.WriteAllText(_filePath, json);
    }

    private static string Normalise(string path)
        => path.ToLowerInvariant();
}
