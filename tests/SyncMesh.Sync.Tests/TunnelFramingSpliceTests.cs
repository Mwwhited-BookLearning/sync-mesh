using System.Net.Sockets;
using SyncMesh.Contracts.Tunnel;

namespace SyncMesh.Sync.Tests;

// Regression coverage for the 2026-07-28 review finding: SpliceAsync used
// to race Task.WhenAny and immediately dispose BOTH streams the instant
// either direction hit EOF. A client that legitimately half-closes one
// direction first (send a request, shutdown-send, then read the
// response — ordinary HTTP-style behavior) would have its response
// truncated, because the client's own EOF tore down the connection to
// the downstream target before the target ever got to reply. See
// TunnelFraming.SpliceAsync's doc comment for the fix (WhenAll + forward
// each EOF as a real half-close on the other leg).
public sealed class TunnelFramingSpliceTests
{
    [Fact]
    public async Task ClientHalfClosingSendFirst_StillReceivesTheFullResponse()
    {
        var request = "request-payload"u8.ToArray();
        var response = new byte[64 * 1024];
        Random.Shared.NextBytes(response);

        // The "downstream target" this tunnel session forwards to: reads
        // the request until the peer's send-side is shut down (a real
        // half-close, not just "no more bytes right now"), then writes
        // back a large response and closes.
        await using var target = TargetServer.Start(response);

        using var relayListener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        relayListener.Start();
        var relayPort = ((System.Net.IPEndPoint)relayListener.LocalEndpoint).Port;

        using var relayCts = new CancellationTokenSource();
        var relayTask = Task.Run(async () =>
        {
            using var clientLeg = await relayListener.AcceptTcpClientAsync(relayCts.Token);
            using var targetLeg = new TcpClient();
            await targetLeg.ConnectAsync(System.Net.IPAddress.Loopback, target.Port, relayCts.Token);
            await TunnelFraming.SpliceAsync(clientLeg.GetStream(), targetLeg.GetStream(), relayCts.Token);
        }, relayCts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, relayPort);
        var clientStream = client.GetStream();

        await clientStream.WriteAsync(request);
        client.Client.Shutdown(SocketShutdown.Send);

        using var received = new MemoryStream();
        await clientStream.CopyToAsync(received);

        Assert.Equal(response, received.ToArray());

        await relayCts.CancelAsync();
    }

    private sealed class TargetServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private TargetServer(TcpListener listener, byte[] response, int port)
        {
            _listener = listener;
            Port = port;
            _acceptLoop = RunAsync(response, _cts.Token);
        }

        public int Port { get; }

        public static TargetServer Start(byte[] response)
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            return new TargetServer(listener, response, port);
        }

        private async Task RunAsync(byte[] response, CancellationToken ct)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();

                // Read the full request — this only returns once the
                // peer's send side is genuinely shut down. Before the
                // fix, the relay would have already disposed this
                // connection at this same moment (WhenAny raced on
                // whichever direction hit EOF first), so this read would
                // either never complete or the socket would already be
                // gone.
                using var requestBuffer = new MemoryStream();
                await stream.CopyToAsync(requestBuffer, ct);

                await stream.WriteAsync(response, ct);
            }
            catch (OperationCanceledException)
            {
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
}
