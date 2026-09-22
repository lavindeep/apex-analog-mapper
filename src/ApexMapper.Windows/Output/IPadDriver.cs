using ApexMapper.Core.Engine;

namespace ApexMapper.Windows.Output;

/// <summary>
/// One virtual Xbox 360 pad as the driver sees it, plus reading it back the way a game
/// does. <see cref="VirtualPad"/> holds every rule; this seam only exists so those rules
/// have tests without ViGEmBus. Any call may throw the driver's own exceptions.
/// </summary>
internal interface IPadDriver : IDisposable
{
    void Connect();

    /// <summary>The XInput slot Windows gave the pad, or -1 until the driver reports it.</summary>
    int UserIndex { get; }

    /// <summary>Sets every channel and submits them in one report.</summary>
    void Submit(in PadReport report);

    void Disconnect();

    /// <summary>What a game reads from an XInput slot; false when nothing is connected there.</summary>
    bool TryReadBack(int userIndex, out PadReport report, out uint packetNumber);
}
