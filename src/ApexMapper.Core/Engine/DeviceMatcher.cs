namespace ApexMapper.Core.Engine;

public sealed record DeviceMatcher(
    int VendorId,
    int ProductId,
    string? SerialNumber,
    string? ProductNamePattern);
