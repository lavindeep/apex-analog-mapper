using ApexMapper.Core.Sensors;

namespace ApexMapper.Windows.Hid;

/// <summary>What a keyboard answered to the firmware request.</summary>
/// <param name="Version">The firmware version, or null when there was no reply or it was not a version.</param>
/// <param name="Reply">The reply as it came, for the capture export, or null when none came.</param>
/// <param name="Problem">Why there is no version, or null.</param>
public sealed record FirmwareReading(string? Version, byte[]? Reply, string? Problem);

/// <summary>
/// Asks a keyboard for its firmware version and nothing else: the only request the app
/// sends to an unverified board before the user agrees to more (try-it step 1). Opens
/// the vendor interface, sends 0x90, reads one reply, and closes it. Must not run while
/// a poller has the same board open, or each would read the other's replies.
/// </summary>
public static class FirmwareProbe
{
    public static FirmwareReading Read(Guid keyboard) => Read(() => HidVendorDevices.Open(keyboard));

    internal static FirmwareReading Read(Func<IVendorStream?> open)
    {
        IVendorStream? stream;
        try
        {
            stream = open();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new FirmwareReading(null, null, "The keyboard's sensor interface could not be opened: " + e.Message);
        }
        if (stream is null)
        {
            return new FirmwareReading(null, null, SensorPoller.WaitingReason);
        }
        using var device = new VendorInterface(stream);
        var reply = new byte[SensorProtocol.ReportLength];
        var status = device.Exchange(SensorRequest.Firmware(), reply);
        if (status != ExchangeStatus.Ok)
        {
            return new FirmwareReading(null, null, SensorPoller.Describe(status));
        }
        return SensorProtocol.ParseFirmware(reply, out var version) is { } error
            ? new FirmwareReading(null, reply, error)
            : new FirmwareReading(version, reply, null);
    }
}
