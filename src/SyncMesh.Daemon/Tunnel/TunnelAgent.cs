using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.Tunnel;

namespace SyncMesh.Daemon.Tunnel;

// Daemon-side half of the Phase 5 tunnel mechanism — see
// docs/adr/0007-custom-reverse-tunnel-mechanism.md. Runs two independent
// loops: a direct listener (the "fast path," tried first by a remote
// client) and a control-connection loop that dials the nearest server's
// TunnelRelay outbound-only (same "daemon dials out" pattern as the NATS
// leaf node, ADR-0002) and opens a data channel on request. One active
// session per daemon at a time — a deliberate POC simplification (see
// ADR-0007), not a hard design limit. TLS/service-credential auth is
// explicitly out of scope this phase — see PRODUCTION-HARDENING.md.
//
// Deliberately has zero reference to NatsConnection/NatsJSContext or
// anything in Daemon/Nats/ — this is what makes the tunnel's failure
// domain independent of event-sync architecturally, not just by
// assertion (see the two SyncMesh.Sync.Tests.TunnelFailureIsolationTests).
public sealed class TunnelAgent(
    IOptions<DaemonOptions> daemonOptions,
    IOptions<TunnelAgentOptions> options,
    ILogger<TunnelAgent> logger) : BackgroundService
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private volatile bool _connectedToRelay;
    private volatile bool _sessionActive;

    public bool ConnectedToRelay => _connectedToRelay;
    public bool SessionActive => _sessionActive;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunDirectListenerLoopAsync(stoppingToken),
            RunControlConnectionLoopAsync(stoppingToken));

    private async Task RunDirectListenerLoopAsync(CancellationToken ct)
    {
        var opts = options.Value;
        while (!ct.IsCancellationRequested)
        {
            var listener = TunnelSockets.CreateDualStackListener(opts.DirectListenPort);
            try
            {
                listener.Start();
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleDirectSessionAsync(client, opts, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tunnel direct listener faulted; restarting in {Delay}.", opts.ReconnectDelay);
                await Task.Delay(opts.ReconnectDelay, ct);
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private async Task HandleDirectSessionAsync(TcpClient client, TunnelAgentOptions opts, CancellationToken ct)
    {
        if (!await _sessionLock.WaitAsync(0, ct))
        {
            // Another session (direct or relayed) is already active — one
            // active session per daemon for this phase. Reject immediately.
            client.Dispose();
            return;
        }

        _sessionActive = true;
        try
        {
            using var target = await ConnectToLocalTargetAsync(opts, ct);
            using (client)
            {
                await TunnelFraming.SpliceAsync(client.GetStream(), target.GetStream(), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Direct tunnel session ended with an error.");
        }
        finally
        {
            _sessionActive = false;
            _sessionLock.Release();
        }
    }

    private async Task RunControlConnectionLoopAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var (relayHost, relayPort) = ParseEndpoint(opts.RelayUrl);

        while (!ct.IsCancellationRequested)
        {
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var control = new TcpClient();
                await control.ConnectAsync(relayHost, relayPort, ct);
                _connectedToRelay = true;

                var stream = control.GetStream();
                await TunnelFraming.WriteFrameAsync(
                    stream, TunnelFrameType.Hello,
                    TunnelFraming.EncodeIdentity(daemonOptions.Value.SiteId, daemonOptions.Value.InstanceId), ct);

                var heartbeatTask = SendHeartbeatsAsync(stream, opts.HeartbeatInterval, heartbeatCts.Token);

                while (!ct.IsCancellationRequested)
                {
                    var (type, _) = await TunnelFraming.ReadFrameAsync(stream, ct);
                    if (type == TunnelFrameType.OpenDataChannel)
                    {
                        _ = HandleDataChannelRequestAsync(opts, relayHost, relayPort, ct);
                    }
                }

                heartbeatCts.Cancel();
                await heartbeatTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tunnel control connection to relay faulted; reconnecting in {Delay}.", opts.ReconnectDelay);
            }
            finally
            {
                _connectedToRelay = false;
                heartbeatCts.Cancel();
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(opts.ReconnectDelay, ct);
            }
        }
    }

    private async Task HandleDataChannelRequestAsync(TunnelAgentOptions opts, string relayHost, int relayPort, CancellationToken ct)
    {
        if (!await _sessionLock.WaitAsync(0, ct))
        {
            // Already busy — the relay should have gated this itself
            // (see TunnelRelay's own per-agent semaphore), but reject
            // defensively rather than trust the other side unconditionally.
            return;
        }

        _sessionActive = true;
        try
        {
            using var dataChannel = new TcpClient();
            await dataChannel.ConnectAsync(relayHost, relayPort, ct);
            var dataStream = dataChannel.GetStream();
            await TunnelFraming.WriteFrameAsync(
                dataStream, TunnelFrameType.DataChannelHello,
                TunnelFraming.EncodeIdentity(daemonOptions.Value.SiteId, daemonOptions.Value.InstanceId), ct);

            using var target = await ConnectToLocalTargetAsync(opts, ct);
            await TunnelFraming.SpliceAsync(dataStream, target.GetStream(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Relayed tunnel session ended with an error.");
        }
        finally
        {
            _sessionActive = false;
            _sessionLock.Release();
        }
    }

    private static async Task SendHeartbeatsAsync(Stream stream, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await TunnelFraming.WriteFrameAsync(stream, TunnelFrameType.Heartbeat, ReadOnlyMemory<byte>.Empty, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Control connection closed/faulted — the outer loop's own
            // read will observe the same failure and handle reconnect.
        }
    }

    private static async Task<TcpClient> ConnectToLocalTargetAsync(TunnelAgentOptions opts, CancellationToken ct)
    {
        var (host, port) = ParseEndpoint(opts.LocalTargetEndpoint);
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        return client;
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var separatorIndex = endpoint.LastIndexOf(':');
        var host = endpoint[..separatorIndex];
        var port = int.Parse(endpoint[(separatorIndex + 1)..]);
        return (host, port);
    }
}
