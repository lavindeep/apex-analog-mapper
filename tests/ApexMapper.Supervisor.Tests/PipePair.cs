using System.IO.Pipes;

namespace ApexMapper.Supervisor.Tests;

/// <summary>
/// A connected, full-duplex transport pair built from a real loopback named
/// pipe — the same transport the supervisor uses. On Unix the runtime backs
/// named pipes with domain sockets, so it works cross-platform.
/// </summary>
internal sealed class PipePair : IAsyncDisposable
{
    private PipePair(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        Server = server;
        Client = client;
    }

    public NamedPipeServerStream Server { get; }

    public NamedPipeClientStream Client { get; }

    public static async Task<PipePair> CreateAsync()
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
        return new PipePair(server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Client.DisposeAsync();
    }
}
