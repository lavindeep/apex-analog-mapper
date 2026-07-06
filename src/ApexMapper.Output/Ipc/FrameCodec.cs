using System.Buffers.Binary;
using MessagePack;

namespace ApexMapper.Output.Ipc;

/// <summary>
/// Length-prefixed MessagePack framing over an arbitrary duplex stream.
///
/// Wire format: a 4-byte little-endian unsigned payload length N (which
/// <em>excludes</em> the prefix itself), followed by N bytes of a
/// MessagePack-serialized <see cref="IFrame"/> union.
///
/// The codec is deliberately allocation-relaxed: the IPC cadence is 100 ms
/// control / 250 ms heartbeat, not the sub-millisecond input hot path, so
/// clarity wins over pooling here. Standard (attribute-driven) MessagePack
/// options are used on purpose — contractless/typeless resolution is avoided so
/// the wire layout stays an explicit, numbered [Key]/[Union] contract that
/// cannot silently drift with a refactor.
/// </summary>
public sealed class FrameCodec
{
    /// <summary>
    /// Upper bound on a single frame's MessagePack payload. Control frames are a
    /// few dozen bytes; the cap exists only to bound a corrupt or hostile length
    /// prefix so a bad peer cannot force an unbounded allocation.
    /// </summary>
    public const int MaxFrameBytes = 64 * 1024;

    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    /// <summary>
    /// Serializes <paramref name="frame"/> and writes it (length prefix + payload)
    /// to <paramref name="stream"/>. The sender is responsible for stamping
    /// <see cref="IFrame.SchemaVersion"/>; a frame that still carries version 0
    /// throws <see cref="InvalidOperationException"/> to catch forgot-to-stamp
    /// bugs at their source.
    ///
    /// <paramref name="cancellationToken"/> aborts <em>before</em> any byte reaches
    /// the wire (during serialization and the cap check). Once the first byte is
    /// written the frame is committed and the prefix + payload + flush run to
    /// completion under <see cref="CancellationToken.None"/>: a length-prefixed
    /// stream cannot survive a half-written frame, so a cancel must never tear one
    /// onto the wire. A transport failure during the write still propagates.
    /// </summary>
    public async ValueTask WriteFrameAsync(Stream stream, IFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SchemaVersion == 0)
        {
            throw new InvalidOperationException(
                "Refusing to write an unstamped frame (SchemaVersion 0); the sender must stamp IFrame.CurrentSchemaVersion.");
        }

        byte[] payload = MessagePackSerializer.Serialize(frame, Options, cancellationToken);
        if (payload.Length > MaxFrameBytes)
        {
            throw new FrameProtocolException(
                $"Serialized frame is {payload.Length} bytes, exceeding the {MaxFrameBytes}-byte cap.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix, CancellationToken.None).ConfigureAwait(false);
        await stream.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame from <paramref name="stream"/>. Returns <c>null</c> on a
    /// clean end-of-stream at a frame boundary (peer closed between frames). A
    /// truncated prefix or body, a zero or oversize length, or a payload that
    /// fails to deserialize all throw <see cref="FrameProtocolException"/>.
    /// </summary>
    public async ValueTask<IFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] prefix = new byte[4];
        int prefixRead = await ReadUpToAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0)
        {
            return null;
        }

        if (prefixRead < prefix.Length)
        {
            throw new FrameProtocolException(
                $"Truncated length prefix: expected 4 bytes at a frame boundary, got {prefixRead}.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0)
        {
            throw new FrameProtocolException("Frame declares a zero-length payload.");
        }

        if (length > MaxFrameBytes)
        {
            throw new FrameProtocolException(
                $"Frame declares a {length}-byte payload, exceeding the {MaxFrameBytes}-byte cap.");
        }

        byte[] payload = new byte[length];
        int bodyRead = await ReadUpToAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (bodyRead < payload.Length)
        {
            throw new FrameProtocolException(
                $"Truncated frame body: expected {payload.Length} bytes, got {bodyRead} before end-of-stream.");
        }

        try
        {
            return MessagePackSerializer.Deserialize<IFrame>(payload, Options, cancellationToken);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new FrameProtocolException("Frame payload is not a valid IFrame.", ex);
        }
    }

    /// <summary>
    /// True when <paramref name="frame"/>'s version is one this build understands
    /// (1..<see cref="IFrame.CurrentSchemaVersion"/>). Version 0 (pre-versioning
    /// peer) and any future version fall outside the known range; the read policy
    /// for those frames lives in the connection consumer, not here.
    /// </summary>
    public static bool IsKnownVersion(IFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.SchemaVersion is >= 1 and <= IFrame.CurrentSchemaVersion;
    }

    /// <summary>
    /// Reads into <paramref name="buffer"/> until it is full or the stream ends,
    /// returning the number of bytes actually read (0 means a clean end-of-stream
    /// before any byte of this buffer).
    /// </summary>
    private static async ValueTask<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
