namespace ApexMapper.Persistence.Devices;

public sealed record DeviceIdentity(
    int VendorId,
    int ProductId,
    string? SerialNumber,
    string? ManufacturerName,
    string? ProductName);
