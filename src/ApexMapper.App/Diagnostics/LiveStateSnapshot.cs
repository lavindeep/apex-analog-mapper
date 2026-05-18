using ApexMapper.Core;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;

namespace ApexMapper.App.Diagnostics;

/// <summary>
/// Snapshot of the runtime state shown in the live-state diagnostics view.
/// Consumed by the live-state diagnostics view. <see cref="Foreground"/>
/// reuses the existing <see cref="ForegroundContext"/> type from
/// <c>ApexMapper.Core</c> rather than introducing a duplicate.
/// </summary>
public sealed record LiveStateSnapshot(
    IReadOnlyList<KeyId> PressedKeys,
    VirtualPadState Pad,
    ForegroundContext? Foreground,
    DateTime CapturedAtUtc);
