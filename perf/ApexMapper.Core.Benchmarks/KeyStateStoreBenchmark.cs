using ApexMapper.Core.Keys;
using BenchmarkDotNet.Attributes;

namespace ApexMapper.Core.Benchmarks;

[MemoryDiagnoser]
public class KeyStateStoreBenchmark
{
    private KeyStateStore _store = null!;
    private KeyId _hotKey;

    [GlobalSetup]
    public void Setup()
    {
        var keys = new[]
        {
            KeyId.FromScanCode(0x11),  // W
            KeyId.FromScanCode(0x1F),  // S
            KeyId.FromScanCode(0x1E),  // A
            KeyId.FromScanCode(0x20),  // D
            KeyId.FromScanCode(0x39),  // Space
        };
        _store = new KeyStateStore(new KeyIndex(keys));
        _hotKey = keys[0];
        _store.Set(_hotKey, 0.5f, KeyProvenance.Analog);
    }

    [Benchmark]
    public KeyState Get() => _store.Get(_hotKey);

    [Benchmark]
    public void Set() => _store.Set(_hotKey, 0.5f, KeyProvenance.Analog);
}
