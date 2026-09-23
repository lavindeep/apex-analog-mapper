using ApexMapper.Windows.Input;

namespace ApexMapper.Windows.Tests.Input;

/// <summary>A window tree: window to process id, process id to image path and elevation, frame host to CoreWindow child.</summary>
internal sealed class FakeWindows : IWindowSystem
{
    public const string Forza = @"C:\Games\ForzaHorizon6\ForzaHorizon6.exe";
    public const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";
    public const string Explorer = @"C:\Windows\explorer.exe";

    public Dictionary<nint, uint> Owners { get; } = new();

    public Dictionary<uint, string> Paths { get; } = new();

    public Dictionary<uint, bool?> Elevation { get; } = new();

    public Dictionary<nint, nint> CoreWindows { get; } = new();

    /// <summary>What this process's own token says; null for unreadable.</summary>
    public bool? OwnElevation { get; set; } = false;

    /// <summary>A plain game window owned by process 4242 at window 100, and Explorer at window 300.</summary>
    public static FakeWindows WithGameAndDesktop()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 4242;
        windows.Paths[4242] = Forza;
        windows.Owners[300] = 7;
        windows.Paths[7] = Explorer;
        return windows;
    }

    public uint ProcessIdOf(nint window) => Owners.GetValueOrDefault(window);

    public string? ImagePathOf(uint processId) => Paths.GetValueOrDefault(processId);

    public nint CoreWindowChildOf(nint window) => CoreWindows.GetValueOrDefault(window);

    public bool? IsElevated(uint processId) => Elevation.TryGetValue(processId, out var value) ? value : false;

    public bool? IsCurrentProcessElevated() => OwnElevation;
}
