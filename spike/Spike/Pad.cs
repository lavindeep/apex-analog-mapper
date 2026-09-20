using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.Xbox360.Exceptions;

namespace Spike;

// ViGEm Xbox 360 pad with the neutral-readback connect sequence from the reference.
internal sealed class Pad : IDisposable
{
    private readonly ViGEmClient _client = new();
    private readonly IXbox360Controller _pad;

    public int Slot { get; private set; } = -1;

    public Pad()
    {
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.Connect();
        InitializeNeutral();
    }

    private void InitializeNeutral()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            int slot;
            try
            {
                slot = _pad.UserIndex;
            }
            catch (Xbox360UserIndexNotReportedException)
            {
                Thread.Sleep(10);
                continue;
            }
            Slot = slot;
            if (Native.XInputGetState((uint)slot, out var s) == 0)
            {
                var g = s.Gamepad;
                if (g.wButtons == 0 && g.bLeftTrigger == 0 && g.bRightTrigger == 0
                    && g.sThumbLX == 0 && g.sThumbLY == 0 && g.sThumbRX == 0 && g.sThumbRY == 0)
                {
                    return;
                }
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 1);
                _pad.SubmitReport();
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _pad.SubmitReport();
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException("Pad did not read neutral within 2 s.");
    }

    public void SetRightTrigger(byte v)
    {
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, v);
        _pad.SubmitReport();
    }

    public void SetLeftX(short v)
    {
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, v);
        _pad.SubmitReport();
    }

    public void Dispose()
    {
        try
        {
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, 0);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
            _pad.SubmitReport();
            _pad.Disconnect();
        }
        catch
        {
        }
        _client.Dispose();
    }
}
