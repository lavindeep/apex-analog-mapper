namespace ApexMapper.Core.Sensors;

/// <summary>What the app knows about SteelSeries product ids and firmware versions.</summary>
public static class KnownKeyboards
{
    public const ushort SteelSeriesVendorId = 0x1038;

    public sealed record Model(ushort ProductId, string Name, bool Verified);

    private static readonly Model[] Models =
    [
        new(0x1610, "Apex Pro", Verified: true),
        new(0x1614, "Apex Pro TKL", Verified: true),
        new(0x1628, "Apex Pro TKL (2023)", Verified: false),
        new(0x1630, "Apex Pro TKL Wireless (2023)", Verified: false),
        new(0x1632, "Apex Pro TKL Wireless (2023)", Verified: false),
        new(0x1640, "Apex Pro Gen 3", Verified: false),
        new(0x1642, "Apex Pro TKL Gen 3", Verified: false),
        new(0x1644, "Apex Pro TKL Wireless Gen 3", Verified: false),
        new(0x1646, "Apex Pro TKL Wireless Gen 3", Verified: false),
    ];

    /// <summary>Firmware strings the sensor protocol has been verified against.</summary>
    public static readonly IReadOnlySet<string> VerifiedFirmware = new HashSet<string>(StringComparer.Ordinal) { "4.9.1", "4.16.8" };

    public static Model? Find(ushort productId) => Models.FirstOrDefault(m => m.ProductId == productId);

    /// <summary>True for a known Apex Pro id, verified or not.</summary>
    public static bool IsApexPro(ushort productId) => Find(productId) is not null;

    public static bool IsVerified(ushort productId, string firmware) =>
        Find(productId) is { Verified: true } && VerifiedFirmware.Contains(firmware);
}
