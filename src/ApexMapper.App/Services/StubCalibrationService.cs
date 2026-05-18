using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// Stub <see cref="ICalibrationService"/> used until Phase 3 wires
/// <see cref="CalibrationService"/> with a real <c>IHidAnalogProbe</c>.
/// All methods throw <see cref="NotSupportedException"/> to surface misconfiguration.
/// </summary>
internal sealed class StubCalibrationService : ICalibrationService
{
    private readonly ILogger<StubCalibrationService> _logger;

    public StubCalibrationService(ILogger<StubCalibrationService> logger)
    {
        _logger = logger;
        _logger.LogWarning("StubCalibrationService active — calibration is disabled until Phase 3 integration.");
    }

    public Task<CalibrationSnapshot> CaptureRestAsync(Guid deviceId, CancellationToken ct)
        => Task.FromException<CalibrationSnapshot>(NotSupported());

    public Task<CalibrationSnapshot> CaptureMaxAsync(Guid deviceId, CancellationToken ct)
        => Task.FromException<CalibrationSnapshot>(NotSupported());

    public Task<CalibrationSnapshot> CaptureNoiseAsync(Guid deviceId, CancellationToken ct)
        => Task.FromException<CalibrationSnapshot>(NotSupported());

    public Task PersistAsync(
        Guid deviceId,
        CalibrationSnapshot rest,
        CalibrationSnapshot max,
        CalibrationSnapshot noise,
        CancellationToken ct)
        => Task.FromException(NotSupported());

    private static NotSupportedException NotSupported()
        => new("CalibrationService is not available. IHidAnalogProbe requires Phase 3 integration.");
}
