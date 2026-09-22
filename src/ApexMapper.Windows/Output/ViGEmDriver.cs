using ApexMapper.Core.Engine;
using ApexMapper.Windows.Native;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360.Exceptions;

namespace ApexMapper.Windows.Output;

/// <summary>
/// ViGEmBus through Nefarius.ViGEm.Client, and XInput for the readback. Auto-submit is
/// off, so a report reaches the driver only through <see cref="Submit"/>, all channels
/// at once.
/// </summary>
internal sealed class ViGEmDriver : IPadDriver
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;

    /// <summary>Opens the bus. Throws the client's exception when ViGEmBus is missing or refuses.</summary>
    public ViGEmDriver()
    {
        _client = new ViGEmClient();
        try
        {
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    public int UserIndex
    {
        get
        {
            try
            {
                return _pad.UserIndex;
            }
            catch (Xbox360UserIndexNotReportedException)
            {
                return -1;
            }
        }
    }

    public void Connect() => _pad.Connect();

    public void Submit(in PadReport report)
    {
        _pad.LeftThumbX = report.LeftStickX;
        _pad.LeftThumbY = report.LeftStickY;
        _pad.RightThumbX = report.RightStickX;
        _pad.RightThumbY = report.RightStickY;
        _pad.LeftTrigger = report.LeftTrigger;
        _pad.RightTrigger = report.RightTrigger;
        _pad.SetButtonsFull(report.Buttons);
        _pad.SubmitReport();
    }

    public void Disconnect() => _pad.Disconnect();

    public bool TryReadBack(int userIndex, out PadReport report, out uint packetNumber)
    {
        if (userIndex < 0 || XInput.XInputGetState((uint)userIndex, out var state) != XInput.ERROR_SUCCESS)
        {
            report = default;
            packetNumber = 0;
            return false;
        }
        var g = state.Gamepad;
        report = new PadReport(g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY, g.bLeftTrigger, g.bRightTrigger, g.wButtons);
        packetNumber = state.dwPacketNumber;
        return true;
    }

    public void Dispose()
    {
        (_pad as IDisposable)?.Dispose();
        _client.Dispose();
    }
}
