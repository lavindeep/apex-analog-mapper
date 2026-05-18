using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Pipeline;

namespace ApexMapper.Input.Abstractions.Tests.Fakes;

public sealed class FakeRawInputAdapter : IRawInputAdapter
{
    private readonly SpscRingBuffer<RawKeyEvent> _ring;

    public FakeRawInputAdapter(SpscRingBuffer<RawKeyEvent> ring)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
    }

    public BackendStatus Status { get; private set; } = BackendStatus.Stopped;

    public bool IsDisposed { get; private set; }

    public event EventHandler<BackendStatusChanged>? StatusChanged;
    public event EventHandler<RawInputDeviceChanged>? DeviceChanged;

    public Task StartAsync(CancellationToken ct)
    {
        SetStatus(BackendStatus.Running, reason: null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        SetStatus(BackendStatus.Stopped, reason: null);
        return Task.CompletedTask;
    }

    public bool Push(in RawKeyEvent ev) => _ring.TryEnqueue(in ev);

    public void Push(RawInputDeviceChanged change)
    {
        DeviceChanged?.Invoke(this, change);
    }

    public void SetStatus(BackendStatus next, string? reason)
    {
        Status = next;
        StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.RawInput, next, reason));
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
