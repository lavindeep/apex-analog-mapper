namespace ApexMapper.App.ViewModels.Calibration;

/// <summary>Scopes a single calibration wizard run to a specific device.</summary>
public sealed record CalibrationWizardOptions(Guid DeviceId);
