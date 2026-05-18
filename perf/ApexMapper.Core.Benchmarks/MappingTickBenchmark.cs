using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using BenchmarkDotNet.Attributes;

namespace ApexMapper.Core.Benchmarks;

[MemoryDiagnoser]
public class MappingTickBenchmark
{
    private BindingPipeline _pipeline = null!;
    private KeyStateStore _store = null!;
    private VirtualPadState _pad;

    [GlobalSetup]
    public void Setup()
    {
        var singles = new[]
        {
            new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f),
            new SingleKeyBinding(KeyId.FromScanCode(0x1F), BindingTarget.LeftTrigger,  LinearCurve.Instance, 120f, 0f),
            new SingleKeyBinding(KeyId.FromScanCode(0x2A), BindingTarget.ButtonLB,     LinearCurve.Instance, 0f,   0f),
            new SingleKeyBinding(KeyId.FromScanCode(0x39), BindingTarget.ButtonRB,     LinearCurve.Instance, 0f,   0f),
            new SingleKeyBinding(KeyId.FromScanCode(0x10), BindingTarget.ButtonB,      LinearCurve.Instance, 0f,   0f),
            new SingleKeyBinding(KeyId.FromScanCode(0x12), BindingTarget.ButtonA,      LinearCurve.Instance, 0f,   0f),
        };
        var axes = new[]
        {
            new AxisPairBinding(KeyId.FromScanCode(0x1E), KeyId.FromScanCode(0x20), BindingTarget.LeftStickX, LinearCurve.Instance, 80f, 80f, SocdMode.Neutral),
        };
        _pipeline = new BindingPipeline(singles, axes);
        _store = new KeyStateStore();
        _store.Set(KeyId.FromScanCode(0x11), 1f, KeyProvenance.Digital);
        _store.Set(KeyId.FromScanCode(0x20), 1f, KeyProvenance.Digital);
        for (var i = 0; i < 100; i++) _pipeline.Tick(_store, 1f, ref _pad);
    }

    [Benchmark]
    public void Tick() => _pipeline.Tick(_store, 1f, ref _pad);
}
