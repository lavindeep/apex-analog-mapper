using System.IO.Pipes;

namespace ApexMapper.Ipc.Tests;

/// <summary>
/// A connected, full-duplex transport pair for framing tests, built from a real
/// loopback named pipe (PipeDirection.InOut on both ends). This exercises the
/// same transport the supervisor uses; on Unix the runtime backs named pipes
/// with domain sockets, so it works cross-platform.
/// </summary>
internal sealed class DuplexStreamPair : IAsyncDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly NamedPipeClientStream _client;

    private DuplexStreamPair(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        _server = server;
        _client = client;
    }

    public Stream EndpointA => _server;

    public Stream EndpointB => _client;

    public static async Task<DuplexStreamPair> CreateAsync()
    {
        // Keep the name short: on Unix the runtime maps a named pipe to a domain
        // socket under the temp dir, and that full path must fit in 104 chars.
        var name = "apx-" + Guid.NewGuid().ToString("N")[..12];
        var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        return new DuplexStreamPair(server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _client.DisposeAsync();
    }
}
