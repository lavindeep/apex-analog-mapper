using System.Buffers.Binary;
using System.Diagnostics;
using HidSharp;
using HidSharp.Reports;

namespace Spike;

// The Apex Pro vendor interface: 65-byte output request, 65-byte input reply.
internal sealed class Vendor : IDisposable
{
    public const int ReportLength = 65;
    public const ushort SteelSeries = 0x1038;

    private readonly HidStream _stream;
    private readonly byte[] _request = new byte[ReportLength];
    private readonly byte[] _drain = new byte[ReportLength];

    public HidDevice Device { get; }
    public int Faults;

    private Vendor(HidDevice device, HidStream stream)
    {
        Device = device;
        _stream = stream;
        _stream.ReadTimeout = 150;
        _stream.WriteTimeout = 150;
    }

    public static IEnumerable<HidDevice> SteelSeriesDevices() =>
        DeviceList.Local.GetHidDevices(SteelSeries);

    public static bool IsVendorInterface(HidDevice d)
    {
        try
        {
            if (d.GetMaxInputReportLength() != ReportLength || d.GetMaxOutputReportLength() != ReportLength)
            {
                return false;
            }
            return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(0xFFC00001u));
        }
        catch
        {
            return false;
        }
    }

    public static Vendor Open()
    {
        var candidates = SteelSeriesDevices().Where(IsVendorInterface).ToList();
        if (candidates.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one vendor interface, found {candidates.Count}.");
        }
        if (!candidates[0].TryOpen(out var stream))
        {
            throw new InvalidOperationException("TryOpen failed on the vendor interface.");
        }
        return new Vendor(candidates[0], stream);
    }

    // Non-blocking-ish drain: read with a 1 ms timeout until nothing comes back.
    public int Drain()
    {
        var count = 0;
        _stream.ReadTimeout = 1;
        try
        {
            while (true)
            {
                try
                {
                    var n = _stream.Read(_drain, 0, ReportLength);
                    if (n <= 0)
                    {
                        break;
                    }
                    count++;
                }
                catch (TimeoutException)
                {
                    break;
                }
            }
        }
        finally
        {
            _stream.ReadTimeout = 150;
        }
        return count;
    }

    // One request and reply. Returns elapsed ticks. Throws on any fault.
    public long Exchange(byte command, byte selector, byte[] reply)
    {
        Array.Clear(_request);
        _request[1] = command;
        _request[2] = selector;
        var t0 = Stopwatch.GetTimestamp();
        _stream.Write(_request, 0, ReportLength);
        var n = _stream.Read(reply, 0, ReportLength);
        var t1 = Stopwatch.GetTimestamp();
        if (n != ReportLength || reply[0] != 0)
        {
            Faults++;
            throw new IOException($"Bad reply: {n} bytes, id {reply[0]}.");
        }
        return t1 - t0;
    }

    public void WriteRequest(byte command, byte selector)
    {
        Array.Clear(_request);
        _request[1] = command;
        _request[2] = selector;
        _stream.Write(_request, 0, ReportLength);
    }

    public void ReadReply(byte[] reply)
    {
        var n = _stream.Read(reply, 0, ReportLength);
        if (n != ReportLength || reply[0] != 0)
        {
            Faults++;
            throw new IOException($"Bad reply: {n} bytes, id {reply[0]}.");
        }
    }

    public string Firmware()
    {
        var reply = new byte[ReportLength];
        Exchange(0x90, 0, reply);
        var end = Array.IndexOf(reply, (byte)0, 1);
        if (end < 0)
        {
            end = ReportLength;
        }
        return System.Text.Encoding.ASCII.GetString(reply, 1, end - 1);
    }

    public long ReadGroup(byte group, byte[] reply)
    {
        var ticks = Exchange(0xD7, group, reply);
        for (var i = 57; i < ReportLength; i++)
        {
            if (reply[i] != 0)
            {
                Faults++;
                throw new IOException("Non-zero padding in sensor reply.");
            }
        }
        return ticks;
    }

    public static ushort Raw(byte[] reply, int slot) => BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(1 + 2 * slot, 2));
    public static ushort Filtered(byte[] reply, int slot) => BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(29 + 2 * slot, 2));

    // Sensor positions for the four racing keys (group, slot), from the firmware map.
    public static readonly (string Key, byte Group, int Slot)[] Wasd =
    [
        ("W", 2, 2), ("A", 3, 1), ("S", 3, 2), ("D", 3, 3),
    ];

    public void Dispose() => _stream.Dispose();
}
