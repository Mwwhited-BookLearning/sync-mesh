using System.Net.Sockets;
using SyncMesh.Contracts.Tunnel;

namespace SyncMesh.TunnelClient;

public sealed class TunnelConnectResult
{
    public required NetworkStream Stream { get; init; }
    public required bool UsedRelay { get; init; }
}

// Direct-first, relay-fallback connection logic — see
// docs/adr/0007-custom-reverse-tunnel-mechanism.md. Shared, not
// re-implemented, by SyncMesh.TunnelClient's own CLI and by the test
// suites (SyncMesh.Sync.Tests, SyncMesh.Bdd.Tests take a ProjectReference
// to this project), so the fallback behavior under test is the literal
// shipped code.
public static class TunnelConnector
{
    public static async Task<TunnelConnectResult> ConnectAsync(
        string directHost, int directPort,
        string relayHost, int relayPort,
        string siteId, string instanceId,
        TimeSpan directAttemptTimeout,
        CancellationToken ct)
    {
        try
        {
            using var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            directCts.CancelAfter(directAttemptTimeout);
            var direct = new TcpClient();
            await direct.ConnectAsync(directHost, directPort, directCts.Token);
            return new TunnelConnectResult { Stream = direct.GetStream(), UsedRelay = false };
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Direct connection unreachable or blocked — fall back to relay.
        }

        var relay = new TcpClient();
        await relay.ConnectAsync(relayHost, relayPort, ct);
        var stream = relay.GetStream();
        await TunnelFraming.WriteFrameAsync(
            stream, TunnelFrameType.ClientHello,
            TunnelFraming.EncodeIdentity(siteId, instanceId), ct);
        return new TunnelConnectResult { Stream = stream, UsedRelay = true };
    }
}
