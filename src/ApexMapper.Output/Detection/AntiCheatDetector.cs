namespace ApexMapper.Output.Detection;

/// <summary>
/// Detects the presence of a running anti-cheat service or a blocklisted title
/// and, when found, tells the session to disable auto-enable.
///
/// The policy is DETECT AND DISABLE, NEVER EVADE: this type only observes the
/// process list and the foreground executable name. It never injects, never
/// reads another process's memory, and never installs a filter — a positive
/// signal turns the mapper off, it does not hide it.
///
/// The scan is FAIL-CLOSED. If the process enumerator throws or hands back a
/// null snapshot we cannot attest that no anti-cheat is running, so the verdict
/// is <see cref="AntiCheatAction.DisableAutoEnable"/> rather than
/// <see cref="AntiCheatAction.Allow"/>. Auto-enable must not proceed on an
/// unverifiable environment; a user can still enable the mapper manually through
/// the confirm path (that is UI-side, not decided here).
/// </summary>
public sealed class AntiCheatDetector
{
    // The four spec service names. Stored normalized (lower-case, no ".exe").
    private static readonly string[] BuiltInServices =
    {
        "BEService.exe",
        "EasyAntiCheat.exe",
        "vgc.exe",
        "FACEITService.exe",
    };

    private readonly IProcessEnumerator _processes;
    private readonly HashSet<string> _serviceNames;
    private readonly HashSet<string> _blockedExecutables;

    public AntiCheatDetector(
        IProcessEnumerator processes,
        IReadOnlyCollection<string>? extraServiceNames = null,
        IReadOnlyCollection<string>? blockedExecutables = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));

        _serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BuiltInServices)
        {
            _serviceNames.Add(NormalizeExecutableName(name));
        }

        if (extraServiceNames is not null)
        {
            foreach (var name in extraServiceNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _serviceNames.Add(NormalizeExecutableName(name));
                }
            }
        }

        _blockedExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (blockedExecutables is not null)
        {
            foreach (var name in blockedExecutables)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _blockedExecutables.Add(NormalizeExecutableName(name));
                }
            }
        }
    }

    public AntiCheatVerdict Evaluate(ForegroundContext? foreground)
    {
        IReadOnlyList<ProcessSnapshot>? snapshot;
        try
        {
            snapshot = _processes.Enumerate();
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }

        if (snapshot is null)
        {
            return Unavailable("the process enumerator returned no snapshot");
        }

        foreach (var process in snapshot)
        {
            if (process is not null && _serviceNames.Contains(NormalizeExecutableName(process.Name)))
            {
                return new AntiCheatVerdict(
                    AntiCheatAction.DisableAutoEnable,
                    $"An anti-cheat service is running ({process.Name}). Auto-enable is disabled.",
                    process.Name);
            }
        }

        if (_blockedExecutables.Count > 0 && foreground?.ExecutablePath is { } path)
        {
            var fileName = FileName(path);
            if (fileName.Length > 0 && _blockedExecutables.Contains(NormalizeExecutableName(fileName)))
            {
                return new AntiCheatVerdict(
                    AntiCheatAction.DisableAutoEnable,
                    $"The foreground application ({fileName}) is on the anti-cheat blocklist. Auto-enable is disabled.",
                    fileName);
            }
        }

        return new AntiCheatVerdict(AntiCheatAction.Allow, null, null);
    }

    private static AntiCheatVerdict Unavailable(string detail) => new(
        AntiCheatAction.DisableAutoEnable,
        $"Anti-cheat scan unavailable ({detail}); auto-enable is disabled because absence cannot be attested.",
        null);

    // Compares executable names tolerating a present-or-absent ".exe" suffix.
    private static string NormalizeExecutableName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    // Extracts the file-name component tolerating both '/' and '\' separators,
    // because a foreground path may arrive in either form and the dev/test box
    // is not Windows (Path.GetFileName would not split on '\' there).
    private static string FileName(string path)
    {
        var lastSlash = path.LastIndexOfAny(new[] { '/', '\\' });
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }
}
