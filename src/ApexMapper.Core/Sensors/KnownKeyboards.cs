namespace ApexMapper.Core.Sensors;

/// <summary>What is known about the sensor protocol on a model.</summary>
public enum SensorSupport
{
    /// <summary>No one has measured it. The app offers the try-it flow.</summary>
    Unverified,
    /// <summary>The sensor protocol was measured on this model.</summary>
    Verified,
    /// <summary>The model's firmware has no sensor request anyone has found, so the app never sends one.</summary>
    Unsupported,
}

/// <summary>What the app knows about SteelSeries product ids and firmware versions.</summary>
public static class KnownKeyboards
{
    public const ushort SteelSeriesVendorId = 0x1038;

    /// <param name="Support">What is known about the sensor protocol on this model. Only the original TKL is verified.</param>
    public sealed record Model(ushort ProductId, string Name, SensorSupport Support);

    /// <summary>
    /// The wireless models run 3.x firmware, a different family from the original TKL's
    /// 4.x. Research into SteelSeries firmware images (HallJoy) found no sensor request
    /// in it, and a TKL Wireless Gen 3 on a cable answered 0xD7 with something other
    /// than sensor data (issue #5), so they are unsupported rather than untested.
    /// </summary>
    private static readonly Model[] Models =
    [
        new(0x1610, "Apex Pro", SensorSupport.Unverified),
        new(0x1614, "Apex Pro TKL", SensorSupport.Verified),
        new(0x1628, "Apex Pro TKL (2023)", SensorSupport.Unverified),
        new(0x1630, "Apex Pro TKL Wireless (2023)", SensorSupport.Unsupported),
        new(0x1632, "Apex Pro TKL Wireless (2023)", SensorSupport.Unsupported),
        new(0x1640, "Apex Pro Gen 3", SensorSupport.Unverified),
        new(0x1642, "Apex Pro TKL Gen 3", SensorSupport.Unverified),
        new(0x1644, "Apex Pro TKL Wireless Gen 3", SensorSupport.Unsupported),
        new(0x1646, "Apex Pro TKL Wireless Gen 3", SensorSupport.Unsupported),
    ];

    /// <summary>Firmware strings the sensor protocol has been verified against.</summary>
    public static readonly IReadOnlySet<string> VerifiedFirmware = new HashSet<string>(StringComparer.Ordinal) { "4.9.1", "4.16.8" };

    public static Model? Find(ushort productId) => Models.FirstOrDefault(m => m.ProductId == productId);

    /// <summary>True for a known Apex Pro id, verified or not.</summary>
    public static bool IsApexPro(ushort productId) => Find(productId) is not null;

    public static bool IsVerified(ushort productId, string firmware) =>
        Find(productId) is { Support: SensorSupport.Verified } && VerifiedFirmware.Contains(firmware);
}
