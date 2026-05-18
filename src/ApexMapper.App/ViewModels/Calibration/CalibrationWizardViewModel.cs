using ApexMapper.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Calibration;

/// <summary>Step identifiers for the calibration wizard flow.</summary>
public enum CalibrationWizardStep
{
    Idle,
    Rest,
    Max,
    Noise,
    ConfirmSave,
    Saved,
    Cancelled,
}

/// <summary>
/// Manages step progression for the calibration wizard:
/// Idle → Rest → Max → Noise → ConfirmSave → (Saved | Cancelled).
/// </summary>
public sealed class CalibrationWizardViewModel : ApexMapper.App.ViewModels.ObservableViewModel
{
    private readonly ICalibrationService _service;
    private readonly CalibrationWizardOptions _options;

    private CalibrationWizardStep _current = CalibrationWizardStep.Idle;
    private string _statusMessage = "Press Start to begin calibration.";
    private double _progress = 0.0;

    // Accumulated snapshots — cleared on cancel.
    private CalibrationSnapshot? _restSnapshot;
    private CalibrationSnapshot? _maxSnapshot;
    private CalibrationSnapshot? _noiseSnapshot;

    private CancellationTokenSource? _captureCts;

    public CalibrationWizardViewModel(
        ICalibrationService service,
        CalibrationWizardOptions options)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        StartCommand = new AsyncRelayCommand(ExecuteStartAsync, CanStart);
        NextCommand = new AsyncRelayCommand(ExecuteNextAsync, CanNext);
        CancelCommand = new RelayCommand(ExecuteCancel, CanCancel);
    }

    // -----------------------------------------------------------------------
    // Observable properties
    // -----------------------------------------------------------------------

    public CalibrationWizardStep Current
    {
        get => _current;
        private set
        {
            if (SetProperty(ref _current, value))
            {
                ((AsyncRelayCommand)StartCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)NextCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>0..1 progress within the current capture step.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // -----------------------------------------------------------------------
    // Command implementations
    // -----------------------------------------------------------------------

    private bool CanStart() => Current == CalibrationWizardStep.Idle;

    private async Task ExecuteStartAsync()
    {
        Current = CalibrationWizardStep.Rest;
        await RunCaptureStepAsync().ConfigureAwait(false);
    }

    private bool CanNext() =>
        Current is CalibrationWizardStep.Rest
            or CalibrationWizardStep.Max
            or CalibrationWizardStep.Noise
            or CalibrationWizardStep.ConfirmSave;

    private async Task ExecuteNextAsync()
    {
        switch (Current)
        {
            case CalibrationWizardStep.Rest:
                Current = CalibrationWizardStep.Max;
                await RunCaptureStepAsync().ConfigureAwait(false);
                break;

            case CalibrationWizardStep.Max:
                Current = CalibrationWizardStep.Noise;
                await RunCaptureStepAsync().ConfigureAwait(false);
                break;

            case CalibrationWizardStep.Noise:
                Current = CalibrationWizardStep.ConfirmSave;
                StatusMessage = "All samples captured. Press Next to save, or Cancel to discard.";
                Progress = 1.0;
                break;

            case CalibrationWizardStep.ConfirmSave:
                await ExecutePersistAsync().ConfigureAwait(false);
                break;
        }
    }

    private bool CanCancel() =>
        Current is not CalibrationWizardStep.Idle
            and not CalibrationWizardStep.Saved
            and not CalibrationWizardStep.Cancelled;

    private void ExecuteCancel()
    {
        // Abort any in-progress capture.
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;

        // Discard accumulated snapshots — no persist call.
        _restSnapshot = null;
        _maxSnapshot = null;
        _noiseSnapshot = null;

        Current = CalibrationWizardStep.Cancelled;
        StatusMessage = "Calibration cancelled. No changes were saved.";
        Progress = 0.0;
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs the capture for whichever step <see cref="Current"/> indicates.
    /// Updates <see cref="Progress"/> to 1.0 when the capture finishes (before advancing).
    /// </summary>
    private async Task RunCaptureStepAsync()
    {
        _captureCts?.Dispose();
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;

        try
        {
            Progress = 0.0;
            switch (Current)
            {
                case CalibrationWizardStep.Rest:
                    StatusMessage = "Hold all keys at rest (do not press any keys). Sampling...";
                    _restSnapshot = await _service.CaptureRestAsync(_options.DeviceId, ct).ConfigureAwait(false);
                    Progress = 1.0;
                    StatusMessage = "Rest calibration captured. Press Next to continue.";
                    break;

                case CalibrationWizardStep.Max:
                    StatusMessage = "Press all keys fully. Sampling...";
                    _maxSnapshot = await _service.CaptureMaxAsync(_options.DeviceId, ct).ConfigureAwait(false);
                    Progress = 1.0;
                    StatusMessage = "Max calibration captured. Press Next to continue.";
                    break;

                case CalibrationWizardStep.Noise:
                    StatusMessage = "Hold all keys at rest again for noise measurement. Sampling...";
                    _noiseSnapshot = await _service.CaptureNoiseAsync(_options.DeviceId, ct).ConfigureAwait(false);
                    Progress = 1.0;
                    StatusMessage = "Noise calibration captured. Press Next to continue.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel handled in ExecuteCancel; step state already reset there.
        }
    }

    private async Task ExecutePersistAsync()
    {
        if (_restSnapshot is null || _maxSnapshot is null || _noiseSnapshot is null)
        {
            StatusMessage = "Error: incomplete calibration data. Please restart.";
            return;
        }

        StatusMessage = "Saving calibration data...";
        Progress = 0.0;

        try
        {
            _captureCts?.Dispose();
            _captureCts = new CancellationTokenSource();
            var ct = _captureCts.Token;

            await _service.PersistAsync(
                _options.DeviceId,
                _restSnapshot,
                _maxSnapshot,
                _noiseSnapshot,
                ct).ConfigureAwait(false);

            Progress = 1.0;
            Current = CalibrationWizardStep.Saved;
            StatusMessage = "Calibration saved successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}. Previous calibration restored.";
        }
    }
}
