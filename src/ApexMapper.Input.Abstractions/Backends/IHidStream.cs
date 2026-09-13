namespace ApexMapper.Input.Abstractions.Backends;

public interface IHidStream : IDisposable
{
    int Read(Span<byte> buffer);
    void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("HID output reports are not supported by this stream.");
    void GetFeature(Span<byte> buffer);
    void SetFeature(ReadOnlySpan<byte> buffer);
}
