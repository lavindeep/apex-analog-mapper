namespace ApexMapper.Output.Ipc;

/// <summary>
/// Raised when the framed IPC stream violates the wire contract: a corrupt or
/// hostile length prefix, a truncated frame, or a payload that fails to
/// deserialize. The connection layer treats this as a fatal fault for the
/// affected connection (fail-closed) rather than attempting to resynchronize.
/// </summary>
public sealed class FrameProtocolException : Exception
{
    public FrameProtocolException(string message) : base(message)
    {
    }

    public FrameProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
