namespace ApexMapper.Windows.Hid;

/// <summary>
/// The open vendor interface as a byte stream, in the shape the HID library offers so
/// the adapter is a pass-through and tests can script replies. <see cref="Read"/> blocks up
/// to the stream's timeout and throws <see cref="TimeoutException"/> when it elapses.
/// Disposing from another thread unblocks a pending read with an exception.
/// </summary>
public interface IVendorStream : IDisposable
{
    int Read(byte[] buffer, int offset, int count);

    void Write(byte[] buffer, int offset, int count);
}
