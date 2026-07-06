using ApexMapper.Core.Pipeline;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ApexMapper.Output.ViGEm;

/// <summary>
/// A virtual Xbox 360 controller backed by the ViGEmBus driver.
///
/// The pure state-to-driver-units translation lives in
/// <see cref="Xbox360ReportPacker"/>; this type only owns the driver handles and
/// the connection lifecycle. Reports are submitted atomically: auto-submit is
/// turned off and every channel of a frame is set before a single
/// <c>SubmitReport</c>, so the pad never briefly presents a half-applied state.
///
/// Failure policy matches the fail-closed session contract. <see cref="Connect"/>
/// throws a human-readable exception (and records it in <see cref="LastError"/>)
/// when the driver is missing or the pad cannot be created, so the session fails
/// closed with an actionable message. <see cref="Submit"/> lets driver failures
/// propagate so the session faults and tears the pad down. <see cref="Disconnect"/>
/// is the teardown: it is idempotent and never throws.
/// </summary>
public sealed class ViGEmXboxOutput : IControllerOutput
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private bool _connected;
    private string? _lastError;

    public bool IsConnected => _connected;

    public string? LastError => _lastError;

    public void Connect()
    {
        if (_connected)
        {
            throw new InvalidOperationException("The controller is already connected.");
        }

        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.AutoSubmitReport = false;
            _controller.Connect();

            // Present a neutral pad the instant it appears, so a game never sees
            // an undefined report between plug-in and the first mapped frame.
            ApplyReport(default);
        }
        catch (Exception ex)
        {
            var message = ViGEmFailure.Describe(ex);
            _lastError = message;
            TearDownHandles();
            throw new InvalidOperationException(message, ex);
        }

        _lastError = null;
        _connected = true;
    }

    public void Submit(in VirtualPadState state)
    {
        EnsureConnected();

        // A driver failure here is not swallowed: it propagates so the session
        // faults and the pad is zeroed and disconnected.
        ApplyReport(Xbox360ReportPacker.Pack(state));
    }

    public void Zero()
    {
        EnsureConnected();
        ApplyReport(default);
    }

    public void Disconnect()
    {
        // Teardown: idempotent and quiet. IsConnected flips first so a racing
        // caller cannot submit into a pad that is being torn down. When no
        // handles were ever opened this returns without invoking any driver
        // member, so a never-connected instance stays entirely driver-free.
        _connected = false;
        if (_client is null && _controller is null)
        {
            return;
        }

        TearDownHandles();
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("The controller is not connected.");
        }
    }

    private void ApplyReport(in Xbox360Report report)
    {
        var controller = _controller!;

        controller.SetAxisValue(Xbox360Axis.LeftThumbX, report.LeftStickX);
        controller.SetAxisValue(Xbox360Axis.LeftThumbY, report.LeftStickY);
        controller.SetAxisValue(Xbox360Axis.RightThumbX, report.RightStickX);
        controller.SetAxisValue(Xbox360Axis.RightThumbY, report.RightStickY);

        controller.SetSliderValue(Xbox360Slider.LeftTrigger, report.LeftTrigger);
        controller.SetSliderValue(Xbox360Slider.RightTrigger, report.RightTrigger);

        controller.SetButtonState(Xbox360Button.A, report.A);
        controller.SetButtonState(Xbox360Button.B, report.B);
        controller.SetButtonState(Xbox360Button.X, report.X);
        controller.SetButtonState(Xbox360Button.Y, report.Y);
        controller.SetButtonState(Xbox360Button.LeftShoulder, report.LeftShoulder);
        controller.SetButtonState(Xbox360Button.RightShoulder, report.RightShoulder);
        controller.SetButtonState(Xbox360Button.Start, report.Start);
        controller.SetButtonState(Xbox360Button.Back, report.Back);
        controller.SetButtonState(Xbox360Button.LeftThumb, report.LeftThumb);
        controller.SetButtonState(Xbox360Button.RightThumb, report.RightThumb);
        controller.SetButtonState(Xbox360Button.Guide, report.Guide);
        controller.SetButtonState(Xbox360Button.Up, report.DpadUp);
        controller.SetButtonState(Xbox360Button.Down, report.DpadDown);
        controller.SetButtonState(Xbox360Button.Left, report.DpadLeft);
        controller.SetButtonState(Xbox360Button.Right, report.DpadRight);

        controller.SubmitReport();
    }

    private void TearDownHandles()
    {
        try
        {
            _controller?.Disconnect();
        }
        catch
        {
            // Best-effort: a failing unplug must not block client disposal.
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // Best-effort: nothing actionable if the driver handle will not release.
        }

        _controller = null;
        _client = null;
    }
}
