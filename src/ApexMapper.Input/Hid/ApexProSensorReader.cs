using System.Buffers.Binary;
using System.IO;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Hid;

/// <summary>
/// Reads filtered sensor counts from the legacy 1038:1614 keyboard on firmware 4.9.1.
/// The caller owns the stream, selects its FFC0:0001 interface, sets bounded timeouts,
/// and serializes access. Replies have no command echo and cannot be demultiplexed.
/// Counts are uncalibrated and the firmware does not promise an atomic snapshot.
/// </summary>
public sealed class ApexProSensorReader
{
    private const int ReportLength = 65;
    private readonly IHidStream _stream;
    private bool _firmwareVerified;

    public ApexProSensorReader(DeviceIdentity device, IHidStream stream)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(stream);
        if (device.VendorId != 0x1038 || device.ProductId != 0x1614)
            throw new NotSupportedException("Sensor reads require the legacy Apex Pro TKL 1038:1614.");
        _stream = stream;
    }

    /// <summary>Checks the exact firmware reply before allowing any D7 query.</summary>
    public void VerifyFirmware()
    {
        _firmwareVerified = false;
        Span<byte> response = stackalloc byte[ReportLength];
        Exchange(0x90, 0, response);
        Span<byte> expected = stackalloc byte[ReportLength];
        expected.Clear();
        "4.9.1"u8.CopyTo(expected[1..]);
        if (!response.SequenceEqual(expected))
            throw new NotSupportedException("Sensor reads are verified only for keyboard firmware 4.9.1.");
        _firmwareVerified = true;
    }

    /// <summary>Reads fourteen filtered uint16 counts for one group, numbered 1 through 5.</summary>
    public void ReadFilteredGroup(int group, Span<ushort> destination)
    {
        if (group is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(group));
        if (destination.Length != 14) throw new ArgumentException("Exactly fourteen sample slots are required.", nameof(destination));
        if (!_firmwareVerified) throw new InvalidOperationException("Verify firmware before reading sensors.");

        try
        {
            Span<byte> response = stackalloc byte[ReportLength];
            Exchange(0xD7, (byte)group, response);
            for (var i = 57; i < ReportLength; i++)
                if (response[i] != 0) throw new IOException("Unexpected sensor reply padding.");
            // Native byte 0 is the report slot; filtered samples follow 28 raw bytes.
            for (var i = 0; i < destination.Length; i++)
                destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(29 + 2 * i, 2));
        }
        catch
        {
            _firmwareVerified = false;
            throw;
        }
    }

    private void Exchange(byte command, byte selector, Span<byte> response)
    {
        Span<byte> request = stackalloc byte[ReportLength];
        request.Clear();
        request[1] = command;
        request[2] = selector;
        _stream.Write(request);
        var count = _stream.Read(response);
        if (count != ReportLength || response[0] != 0)
            throw new IOException("Expected a 65-byte unnumbered Apex Pro reply.");
    }
}
