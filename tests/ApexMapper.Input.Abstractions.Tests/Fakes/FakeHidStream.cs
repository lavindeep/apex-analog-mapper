using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.Input.Abstractions.Tests.Fakes;

public sealed class FakeHidStream : IHidStream
{
    private readonly Queue<byte[]> _reports;
    private byte[] _featureResponse = Array.Empty<byte>();
    private readonly List<byte[]> _setFeatureCalls = new();

    public FakeHidStream(IEnumerable<byte[]> reports)
    {
        _reports = new Queue<byte[]>(reports);
    }

    public bool IsDisposed { get; private set; }

    public int GetFeatureCallCount { get; private set; }

    public IReadOnlyList<byte[]> SetFeatureCalls => _setFeatureCalls;

    public void EnqueueReport(byte[] report) => _reports.Enqueue(report);

    public void SetFeatureResponse(byte[] response) => _featureResponse = response;

    public int Read(Span<byte> buffer)
    {
        if (_reports.Count == 0)
        {
            return 0;
        }

        var next = _reports.Dequeue();
        var n = Math.Min(next.Length, buffer.Length);
        next.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public void GetFeature(Span<byte> buffer)
    {
        GetFeatureCallCount++;
        var n = Math.Min(_featureResponse.Length, buffer.Length);
        _featureResponse.AsSpan(0, n).CopyTo(buffer);
    }

    public void SetFeature(ReadOnlySpan<byte> buffer)
    {
        _setFeatureCalls.Add(buffer.ToArray());
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}
