namespace ApexMapper.Output.Detection;

/// <summary>
/// Detects whether the foreground application was launched through Steam.
///
/// This is ADVISORY: a positive verdict only WARNS the user. Unlike
/// <see cref="AntiCheatDetector"/> it is NOT fail-closed — if the process
/// enumerator throws while walking the parent chain we simply skip that signal
/// and fall back to the app-id and library-path signals; a Steam-detection
/// failure never blocks or modifies anything, and this type never touches Steam
/// settings.
///
/// Three independent signals, any of which is sufficient:
///   A) the foreground process's parent-process chain contains steam.exe;
///   B) the foreground carries a SteamAppId;
///   C) the foreground executable lives under a known Steam library root.
///
/// POLICY: we NEVER read another process's environment block. Discovering a
/// SteamAppId that way would require ReadProcessMemory against the game, which
/// violates the no-game-memory-access policy. <see cref="ForegroundContext.SteamAppId"/>
/// is only ever set when the caller obtained it legitimately (e.g. a future
/// launch integration); for foreign processes the enumerator's environment
/// dictionary is expected to be empty and we do not try harder.
/// </summary>
public sealed class SteamDetector
{
    private const int MaxParentHops = 32;
    private const string SteamProcessName = "steam.exe";

    private readonly IProcessEnumerator _processes;
    private readonly IReadOnlyList<string> _libraryRoots;

    public SteamDetector(IProcessEnumerator processes, IReadOnlyCollection<string>? steamLibraryRoots = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _libraryRoots = steamLibraryRoots is null
            ? Array.Empty<string>()
            : steamLibraryRoots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(NormalizePath)
                .Select(r => r.TrimEnd('/'))
                .Where(r => r.Length > 0)
                .ToList();
    }

    public SteamVerdict Evaluate(ForegroundContext foreground)
    {
        ArgumentNullException.ThrowIfNull(foreground);

        var appId = string.IsNullOrWhiteSpace(foreground.SteamAppId) ? null : foreground.SteamAppId;

        // Signal B first: it is the cheapest and carries the app id.
        if (appId is not null)
        {
            return new SteamVerdict(true, appId, $"Steam app id present ({appId}).");
        }

        // Signal C: foreground executable under a Steam library root.
        if (foreground.ExecutablePath is { } path && IsUnderAnyLibraryRoot(path))
        {
            return new SteamVerdict(true, null, "Foreground executable is under a Steam library path.");
        }

        // Signal A: walk the parent chain for steam.exe. Advisory-only, so a
        // throwing enumerator degrades to "no parent signal" rather than faulting.
        if (ParentChainContainsSteam(foreground.ProcessId))
        {
            return new SteamVerdict(true, null, "steam.exe is in the launch chain (the process itself or a parent).");
        }

        return new SteamVerdict(false, null, null);
    }

    private bool ParentChainContainsSteam(int startProcessId)
    {
        var visited = new HashSet<int>();
        var currentId = startProcessId;

        for (var hop = 0; hop < MaxParentHops; hop++)
        {
            if (!visited.Add(currentId))
            {
                return false; // cycle (self-parent or recycled pid) — stop.
            }

            ProcessSnapshot? current;
            try
            {
                current = _processes.GetById(currentId);
            }
            catch (Exception)
            {
                return false; // advisory: a failed lookup just ends the walk.
            }

            if (current is null)
            {
                return false; // missing/reused parent ends the walk cleanly.
            }

            if (string.Equals(current.Name, SteamProcessName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.Name, "steam", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.ParentProcessId == currentId)
            {
                return false; // self-parent short-circuit (also caught by visited).
            }

            currentId = current.ParentProcessId;
        }

        return false; // hop bound reached.
    }

    private bool IsUnderAnyLibraryRoot(string path)
    {
        var normalized = NormalizePath(path);
        foreach (var root in _libraryRoots)
        {
            if (normalized.StartsWith(root, StringComparison.Ordinal)
                && (normalized.Length == root.Length || normalized[root.Length] == '/'))
            {
                return true; // boundary check rejects SteamLibrary2 vs SteamLibrary.
            }
        }

        return false;
    }

    // Lower-cases and normalizes separators to '/', so an ordinal comparison is
    // effectively case-insensitive and slash-agnostic across the two path forms.
    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').ToLowerInvariant();
}
