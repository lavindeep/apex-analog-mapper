using ApexMapper.Core.Sensors;

namespace ApexMapper.Windows.Hid;

public enum ExchangeStatus
{
    Ok,
    /// <summary>No reply within <see cref="VendorInterface.TimeoutMs"/>.</summary>
    Timeout,
    /// <summary>A reply shorter than 65 bytes.</summary>
    ShortReply,
    /// <summary>A reply whose report id was not zero.</summary>
    BadReportId,
    /// <summary>The handle was aborted or the device went away.</summary>
    Closed,
    /// <summary>The device write or read failed for another reason.</summary>
    Failed,
}

/// <summary>
/// One request and one reply on the vendor interface, serialised by the caller. The
/// request goes through <see cref="SensorRequest.WriteTo"/>, which is where the
/// command allowlist lives; nothing else in the process writes to the keyboard. No
/// input-queue drain (stage 0: zero stale replies, 10 ms per drain). <see cref="Abort"/>
/// closes the handle from any thread to unblock a pending read.
/// </summary>
public sealed class VendorInterface : IDisposable
{
    public const int TimeoutMs = 150;

    private readonly IVendorStream _stream;
    private readonly byte[] _request = new byte[SensorProtocol.ReportLength];
    private readonly byte[] _reply = new byte[SensorProtocol.ReportLength];
    private int _closed;

    public VendorInterface(IVendorStream stream)
    {
        _stream = stream;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Writes the request and reads the reply into <paramref name="reply"/> (65 bytes). Allocation-free.</summary>
    public ExchangeStatus Exchange(SensorRequest request, Span<byte> reply)
    {
        if (reply.Length != SensorProtocol.ReportLength)
        {
            throw new ArgumentException("Reply buffer must be 65 bytes.", nameof(reply));
        }
        request.WriteTo(_request);
        if (IsClosed)
        {
            return ExchangeStatus.Closed;
        }
        try
        {
            _stream.Write(_request, 0, _request.Length);
            var read = _stream.Read(_reply, 0, _reply.Length);
            if (IsClosed)
            {
                return ExchangeStatus.Closed;
            }
            if (read != SensorProtocol.ReportLength)
            {
                return ExchangeStatus.ShortReply;
            }
            if (_reply[0] != 0)
            {
                return ExchangeStatus.BadReportId;
            }
            _reply.CopyTo(reply);
            return ExchangeStatus.Ok;
        }
        catch (TimeoutException)
        {
            return IsClosed ? ExchangeStatus.Closed : ExchangeStatus.Timeout;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return IsClosed ? ExchangeStatus.Closed : ExchangeStatus.Failed;
        }
    }

    /// <summary>Closes the handle. A read blocked on the device returns at once with <see cref="ExchangeStatus.Closed"/>.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _stream.Dispose();
        }
    }

    public void Dispose() => Abort();
}
