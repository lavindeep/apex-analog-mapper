namespace ApexMapper.Input.Abstractions.Backends;

public interface IHidStream : IDisposable
{
    int Read(Span<byte> buffer);
    void GetFeature(Span<byte> buffer);
    void SetFeature(ReadOnlySpan<byte> buffer);
}
