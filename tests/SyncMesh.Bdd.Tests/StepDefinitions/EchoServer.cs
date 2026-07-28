using System.Net;
using System.Net.Sockets;
using SyncMesh.Contracts.Tunnel;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// Trivial TCP echo target for tunnel tests — the tunnel forwards raw
// bytes to whatever LocalTargetEndpoint points at (see
// docs/adr/0007-custom-reverse-tunnel-mechanism.md); an echo server makes
// the round-trip trivially verifiable (byte equality) without needing any
// real protocol behind the tunnel.
public sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private EchoServer(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        _acceptLoop = RunAsync(_cts.Token);
    }

    public int Port { get; }

    public static EchoServer Start()
    {
        // Dual-stack (IPv4 + IPv6), same reasoning as TunnelSockets — the
        // tunnel connects to this via "localhost", whose resolution order
        // isn't guaranteed.
        var listener = TunnelSockets.CreateDualStackListener(0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new EchoServer(listener, port);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
