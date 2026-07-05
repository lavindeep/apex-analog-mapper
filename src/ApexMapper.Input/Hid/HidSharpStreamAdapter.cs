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
        catch (TimeoutException)
        {
            // HidSharp throws TimeoutException when the stream's read timeout
            // elapses with no report ready — a healthy but quiet device, not a
            // fault. Normalize it to an idle (0-byte) read so the poll loop treats
            // it as an idle tick instead of a failure. IOException and every other
            // exception still propagate so a genuinely dead transport faults.
            return 0;
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
            // The pooled buffer arrives with arbitrary bytes. HidSharp reads
            // buffer[0] as the report id being requested, so copy the caller's
            // buffer in first (as SetFeature does) — this conveys the requested
            // report id and a clean request payload instead of pool garbage.
            buffer.CopyTo(rented);
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
