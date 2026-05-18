using System.Buffers;
using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.Input.Hid;

/// <summary>
/// Bridges HidSharp's byte[]+offset+count <see cref="HidSharp.HidStream"/> surface to
/// our <see cref="IHidStream"/> Span&lt;byte&gt; surface. Rents pooled buffers for
/// each call to keep the hot path off the GC heap.
/// </summary>
internal sealed class HidSharpStreamAdapter : IHidStream
{
    private readonly HidSharp.HidStream _stream;
    private int _disposed;

    public HidSharpStreamAdapter(HidSharp.HidStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var n = _stream.Read(rented, 0, buffer.Length);
            if (n > 0)
            {
                new ReadOnlySpan<byte>(rented, 0, n).CopyTo(buffer);
            }
            return n;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void GetFeature(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            // HidSharp.GetFeature(buffer, offset, count) fills the buffer; copy the
            // requested length back out.
            _stream.GetFeature(rented, 0, buffer.Length);
            new ReadOnlySpan<byte>(rented, 0, buffer.Length).CopyTo(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void SetFeature(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            _stream.SetFeature(rented, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
        }
    }
}
