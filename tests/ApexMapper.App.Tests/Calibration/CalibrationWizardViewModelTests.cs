using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Calibration;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Calibration;

// ---------------------------------------------------------------------------
// Fake ICalibrationService
// ---------------------------------------------------------------------------

internal sealed class FakeCalibrationService : ICalibrationService
{
    private readonly CalibrationSnapshot _restResult;
    private readonly CalibrationSnapshot _maxResult;
    private readonly CalibrationSnapshot _noiseResult;

    public List<(Guid DeviceId, CalibrationSnapshot Rest, CalibrationSnapshot Max, CalibrationSnapshot Noise)>
        PersistCalls { get; } = new();

    public bool ThrowOnPersist { get; set; }

    public FakeCalibrationService(
        CalibrationSnapshot? restResult = null,
        CalibrationSnapshot? maxResult = null,
        CalibrationSnapshot? noiseResult = null)
    {
        var empty = new CalibrationSnapshot(new Dictionary<byte, ushort>(), DateTimeOffset.UtcNow);
        _restResult = restResult ?? empty;
        _maxResult = maxResult ?? empty;
        _noiseResult = noiseResult ?? empty;
    }

    public Task<CalibrationSnapshot> CaptureRestAsync(Guid deviceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_restResult);
    }

    public Task<CalibrationSnapshot> CaptureMaxAsync(Guid deviceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_maxResult);
    }

    public Task<CalibrationSnapshot> CaptureNoiseAsync(Guid deviceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_noiseResult);
    }

    public Task PersistAsync(
        Guid deviceId,
        CalibrationSnapshot rest,
        CalibrationSnapshot max,
        CalibrationSnapshot noise,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ThrowOnPersist)
            throw new InvalidOperationException("Simulated persist failure.");
        PersistCalls.Add((deviceId, rest, max, noise));
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class CalibrationWizardViewModelTests
{
    private static CalibrationWizardOptions DefaultOptions =>
        new(DeviceId: Guid.NewGuid());

    private static CalibrationWizardViewModel Build(
        FakeCalibrationService? service = null,
        CalibrationWizardOptions? options = null) =>
        new(service ?? new FakeCalibrationService(), options ?? DefaultOptions);

    // -----------------------------------------------------------------------
    // 1. Initial step is Idle
    // -----------------------------------------------------------------------

    [Fact]
    public void Initial_step_is_Idle()
    {
        var vm = Build();

        vm.Current.Should().Be(CalibrationWizardStep.Idle);
        vm.Progress.Should().Be(0.0);
    }

    // -----------------------------------------------------------------------
    // 2. StartCommand advances to Rest
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartCommand_advances_to_Rest()
    {
        var vm = Build();

        vm.StartCommand.CanExecute(null).Should().BeTrue();
        await vm.StartCommand.ExecuteAsync(null);

        // After rest capture completes, Current stays Rest (waiting for NextCommand).
        vm.Current.Should().Be(CalibrationWizardStep.Rest);
    }

    // -----------------------------------------------------------------------
    // 3. NextCommand advances through Rest → Max → Noise → ConfirmSave
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NextCommand_advances_through_Rest_Max_Noise_ConfirmSave()
    {
        var vm = Build();

        await vm.StartCommand.ExecuteAsync(null);
        vm.Current.Should().Be(CalibrationWizardStep.Rest);

        await vm.NextCommand.ExecuteAsync(null);
        vm.Current.Should().Be(CalibrationWizardStep.Max);

        await vm.NextCommand.ExecuteAsync(null);
        vm.Current.Should().Be(CalibrationWizardStep.Noise);

        await vm.NextCommand.ExecuteAsync(null);
        vm.Current.Should().Be(CalibrationWizardStep.ConfirmSave);
    }

    // -----------------------------------------------------------------------
    // 4. ConfirmSave calls PersistAsync with all three snapshots
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConfirmSave_calls_PersistAsync_with_all_snapshots()
    {
        var service = new FakeCalibrationService();
        var options = DefaultOptions;
        var vm = Build(service, options);

        await vm.StartCommand.ExecuteAsync(null);  // → Rest
        await vm.NextCommand.ExecuteAsync(null);   // → Max
        await vm.NextCommand.ExecuteAsync(null);   // → Noise
        await vm.NextCommand.ExecuteAsync(null);   // → ConfirmSave
        await vm.NextCommand.ExecuteAsync(null);   // Persist → Saved

        vm.Current.Should().Be(CalibrationWizardStep.Saved);
        service.PersistCalls.Should().HaveCount(1);
        service.PersistCalls[0].DeviceId.Should().Be(options.DeviceId);
        service.PersistCalls[0].Rest.Should().NotBeNull();
        service.PersistCalls[0].Max.Should().NotBeNull();
        service.PersistCalls[0].Noise.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // 5. CancelCommand rolls back to Idle without persisting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CancelCommand_rolls_back_to_Idle_without_persisting()
    {
        var service = new FakeCalibrationService();
        var vm = Build(service);

        vm.CancelCommand.CanExecute(null).Should().BeFalse("can't cancel from Idle");

        await vm.StartCommand.ExecuteAsync(null);  // → Rest
        vm.CancelCommand.CanExecute(null).Should().BeTrue();
        vm.CancelCommand.Execute(null);

        vm.Current.Should().Be(CalibrationWizardStep.Cancelled);
        service.PersistCalls.Should().BeEmpty();
        vm.Progress.Should().Be(0.0);
    }

    // -----------------------------------------------------------------------
    // 6. Cancel from partway through (after Rest) does not persist
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cancel_from_partway_through_steps_does_not_persist()
    {
        var service = new FakeCalibrationService();
        var vm = Build(service);

        await vm.StartCommand.ExecuteAsync(null);  // → Rest (capture done)
        await vm.NextCommand.ExecuteAsync(null);   // → Max (capture done)

        vm.Current.Should().Be(CalibrationWizardStep.Max);
        vm.CancelCommand.Execute(null);

        vm.Current.Should().Be(CalibrationWizardStep.Cancelled);
        service.PersistCalls.Should().BeEmpty("cancel before ConfirmSave must not persist");
    }

    // -----------------------------------------------------------------------
    // 7. Progress reaches 1.0 at end of each capture step
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Progress_reaches_1_at_end_of_each_capture()
    {
        var progressValues = new List<double>();
        var vm = Build();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Progress))
                progressValues.Add(vm.Progress);
        };

        await vm.StartCommand.ExecuteAsync(null);  // Rest capture
        progressValues.Should().Contain(1.0, "Rest step should reach Progress=1.0");

        progressValues.Clear();
        await vm.NextCommand.ExecuteAsync(null);   // Max capture
        progressValues.Should().Contain(1.0, "Max step should reach Progress=1.0");

        progressValues.Clear();
        await vm.NextCommand.ExecuteAsync(null);   // Noise capture
        progressValues.Should().Contain(1.0, "Noise step should reach Progress=1.0");
    }

    // -----------------------------------------------------------------------
    // Edge: CancelCommand not available after Saved
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CancelCommand_not_available_after_Saved()
    {
        var vm = Build();

        await vm.StartCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);   // → ConfirmSave
        await vm.NextCommand.ExecuteAsync(null);   // → Saved

        vm.CancelCommand.CanExecute(null).Should().BeFalse();
    }
}
