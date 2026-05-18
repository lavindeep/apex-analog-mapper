using ApexMapper.App.Services;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.App.Composition;

/// <summary>
/// Concrete <see cref="IDeviceRegistryFacade"/> that reads calibration state
/// from <see cref="DeviceRegistry"/> on demand.
///
/// Calibration status rules:
/// <list type="bullet">
///   <item>No calibrations in the registry → <see cref="DeviceCalibrationStatus.NotCalibrated"/></item>
///   <item>At least one <see cref="KeyCalibration"/> present → <see cref="DeviceCalibrationStatus.Calibrated"/></item>
///   <item>Registry file unreadable → <see cref="DeviceCalibrationStatus.Faulted"/></item>
/// </list>
/// The device Guid is not stored in the registry file directly; all devices
/// that have calibration data in the file are considered <c>Calibrated</c>.
/// A future revision can key on <see cref="DeviceSelectorFacade.ToGuid"/> once
/// the registry stores per-device calibration records individually.
/// </summary>
public sealed class DeviceRegistryFacade : IDeviceRegistryFacade
{
    private readonly IAppPaths _paths;

    public DeviceRegistryFacade(IAppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public DeviceCalibrationStatus GetStatus(Guid deviceId)
    {
        try
        {
            var registry = DeviceRegistry.Load(_paths.DeviceRegistryFile);
            return registry.Calibrations.Count > 0
                ? DeviceCalibrationStatus.Calibrated
                : DeviceCalibrationStatus.NotCalibrated;
        }
        catch
        {
            return DeviceCalibrationStatus.Faulted;
        }
    }
}
