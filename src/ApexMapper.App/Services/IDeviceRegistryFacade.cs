namespace ApexMapper.App.Services;

/// <summary>
/// App-side read-only view of calibration data stored in
/// <see cref="ApexMapper.Persistence.Devices.DeviceRegistry"/>.
/// Using a facade keeps the ViewModel testable without touching file I/O.
/// </summary>
public interface IDeviceRegistryFacade
{
    /// <summary>Return the calibration status for the device identified by <paramref name="deviceId"/>.</summary>
    DeviceCalibrationStatus GetStatus(Guid deviceId);
}
