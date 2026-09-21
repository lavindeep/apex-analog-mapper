using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Hid;

/// <summary>Stage 0 captures from the maintainer's board.</summary>
public static class Fixtures
{
    private static readonly Dictionary<string, byte[]> Cache = new();

    /// <summary>A fresh copy, safe to mutate.</summary>
    public static byte[] Load(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var bytes))
            {
                bytes = Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".hex")).Trim());
                Cache[name] = bytes;
            }
            return (byte[])bytes.Clone();
        }
    }

    public static byte[] Firmware => Load("firmware");

    public static byte[] RestGroup(int group) => Load("rest-group" + group);

    public static byte[] HeldGroup(int group) => Load("w-held-group" + group);

    public static GroupSignature SignatureOf(int group)
    {
        var raw = new ushort[SensorProtocol.SensorsPerGroup];
        var filtered = new ushort[SensorProtocol.SensorsPerGroup];
        Assert.Null(SensorProtocol.ParseGroup(RestGroup(group), raw, filtered));
        return GroupSignature.FromRest(raw);
    }

    public static IReadOnlyDictionary<int, GroupSignature> Signatures(params int[] groups) =>
        groups.ToDictionary(g => g, SignatureOf);
}

/// <summary>
/// A scripted vendor interface. By default it answers 0x90 with the firmware fixture
/// and 0xD7/N with the rest fixture for group N. <see cref="OnRead"/> can replace a
/// reply (return bytes), simulate a timeout (return null), or block on an event;
/// disposing releases a blocked read with an exception, as the real stream does.
/// </summary>
public sealed class FakeVendorStream : IVendorStream
{
    private static readonly byte[] FirmwareReply = Fixtures.Firmware;
    private static readonly byte[][] GroupReplies = [.. Enumerable.Range(1, SensorRequest.GroupCount).Select(Fixtures.RestGroup)];

    private readonly ManualResetEventSlim _disposed = new(false);
    private byte _command;
    private byte _selector;

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public bool IsDisposed => _disposed.IsSet;

    /// <summary>Called with the read index (0-based), command and selector. Return the reply, or null for a timeout.</summary>
    public Func<int, byte, byte, byte[]?>? OnRead { get; set; }

    /// <summary>The stage 0 reply for a request. Shared arrays: never mutate the result.</summary>
    public static byte[] DefaultReply(byte command, byte selector) => command switch
    {
        SensorRequest.FirmwareCommand => FirmwareReply,
        SensorRequest.GroupCommand => GroupReplies[selector - 1],
        _ => throw new InvalidOperationException($"Unexpected command 0x{command:X2}."),
    };

    /// <summary>A request that was not a full report with report id zero, if any.</summary>
    public bool SawMalformedRequest { get; private set; }

    public void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        SawMalformedRequest |= count != SensorProtocol.ReportLength || buffer[offset] != 0;
        _command = buffer[offset + 1];
        _selector = buffer[offset + 2];
        Writes++;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        var index = Reads++;
        var reply = OnRead is null ? DefaultReply(_command, _selector) : OnRead(index, _command, _selector);
        ThrowIfDisposed();
        if (reply is null)
        {
            throw new TimeoutException("Scripted timeout.");
        }
        var length = Math.Min(reply.Length, count);
        Array.Copy(reply, 0, buffer, offset, length);
        return length;
    }

    /// <summary>Blocks the calling read until the stream is disposed. For use inside <see cref="OnRead"/>.</summary>
    public void BlockUntilDisposed()
    {
        _disposed.Wait();
    }

    /// <summary>Blocks until the gate is set or the stream is disposed.</summary>
    public void BlockUntil(ManualResetEventSlim gate)
    {
        WaitHandle.WaitAny([gate.WaitHandle, _disposed.WaitHandle]);
    }

    public void Dispose() => _disposed.Set();

    private void ThrowIfDisposed()
    {
        if (_disposed.IsSet)
        {
            throw new IOException("The handle was closed.");
        }
    }
}
