using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.Tunnel;

namespace SyncMesh.ServerHost.Tunnel;

// Server-side half of the Phase 5 tunnel mechanism — see
// docs/adr/0007-custom-reverse-tunnel-mechanism.md. Two independent
// accept loops: an agent listener (daemons dial in with a persistent
// control connection, then on demand a data channel) and a client
// listener (remote tunnel clients dial in when direct access is
// blocked). One active session per agent at a time — gated here by a
// per-agent semaphore before ever asking the agent to open a data
// channel, so a busy agent never even sees the second request. TLS/
// service-credential auth is explicitly out of scope this phase — see
// PRODUCTION-HARDENING.md.
//
// Deliberately has zero reference to NatsConnection/NatsJSContext or
// anything in ServerHost/Nats/ — this is what makes the tunnel's failure
// domain independent of event-sync architecturally, not just by
// assertion (see the two SyncMesh.Sync.Tests.TunnelFailureIsolationTests).
public sealed class TunnelRelay(
    IOptions<TunnelRelayOptions> options,
    ILogger<TunnelRelay> logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunAgentListenerLoopAsync(stoppingToken),
            RunClientListenerLoopAsync(stoppingToken));

    private async Task RunAgentListenerLoopAsync(CancellationToken ct)
    {
        var port = options.Value.AgentListenPort;
        while (!ct.IsCancellationRequested)
        {
            var listener = TunnelSockets.CreateDualStackListener(port);
            try
            {
                listener.Start();
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleAgentConnectionAsync(client, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tunnel agent listener faulted; restarting in {Delay}.", RestartDelay);
                await Task.Delay(RestartDelay, ct);
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private async Task HandleAgentConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        try
        {
            var (type, payload) = await TunnelFraming.ReadFrameAsync(stream, ct);
            var identity = TunnelFraming.DecodeIdentity(payload);
            var key = $"{identity.SiteId}.{identity.InstanceId}";

            switch (type)
            {
                case TunnelFrameType.Hello:
                    await HandleControlConnectionAsync(key, stream, client, ct);
                    break;

                case TunnelFrameType.DataChannelHello:
                    // Opened by the agent in response to our own
                    // OpenDataChannel request — hand it to whichever
                    // client session is waiting on it.
                    if (_agents.TryGetValue(key, out var entry) &&
                        Interlocked.Exchange(ref entry.PendingDataChannel, null) is { } pending)
                    {
                        pending.TrySetResult(stream);
                    }
                    else
                    {
                        client.Dispose();
                    }
                    break;

                default:
                    client.Dispose();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent connection ended with an error.");
            client.Dispose();
        }
    }

    private async Task HandleControlConnectionAsync(string key, NetworkStream stream, TcpClient client, CancellationToken ct)
    {
        var entry = new AgentEntry(stream);
        _agents[key] = entry; // register/replace — a reconnecting agent supersedes its own stale entry

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Heartbeat frames only expected here; reading is what
                // detects the connection closing/faulting.
                await TunnelFraming.ReadFrameAsync(stream, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tunnel agent {Key} control connection dropped.", key);
        }
        finally
        {
            _agents.TryRemove(new KeyValuePair<string, AgentEntry>(key, entry));
            client.Dispose();
        }
    }

    private async Task RunClientListenerLoopAsync(CancellationToken ct)
    {
        var opts = options.Value;
        while (!ct.IsCancellationRequested)
        {
            var listener = TunnelSockets.CreateDualStackListener(opts.ClientListenPort);
            try
            {
                listener.Start();
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientConnectionAsync(client, opts, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tunnel client listener faulted; restarting in {Delay}.", RestartDelay);
                await Task.Delay(RestartDelay, ct);
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private async Task HandleClientConnectionAsync(TcpClient client, TunnelRelayOptions opts, CancellationToken ct)
    {
        using (client)
        {
            var clientStream = client.GetStream();
            try
            {
                var (type, payload) = await TunnelFraming.ReadFrameAsync(clientStream, ct);
                if (type != TunnelFrameType.ClientHello)
                {
                    return;
                }

                var identity = TunnelFraming.DecodeIdentity(payload);
                var key = $"{identity.SiteId}.{identity.InstanceId}";

                if (!_agents.TryGetValue(key, out var entry))
                {
                    logger.LogWarning("Tunnel client requested unknown/unreachable agent {Key}.", key);
                    return;
                }

                if (!await entry.SessionLock.WaitAsync(0, ct))
                {
                    // Already busy — one active session per agent for this
                    // phase (see ADR-0007). Reject; the agent is never even
                    // told about this second request.
                    logger.LogWarning("Tunnel agent {Key} is already handling a session; rejecting new client.", key);
                    return;
                }

                try
                {
                    var pending = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
                    entry.PendingDataChannel = pending;
                    await TunnelFraming.WriteFrameAsync(entry.ControlStream, TunnelFrameType.OpenDataChannel, ReadOnlyMemory<byte>.Empty, ct);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(opts.SessionWaitTimeout);
                    await using var dataChannelStream = await pending.Task.WaitAsync(timeoutCts.Token);

                    await TunnelFraming.SpliceAsync(clientStream, dataChannelStream, ct);
                }
                finally
                {
                    entry.SessionLock.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tunnel client session ended with an error.");
            }
        }
    }

    private sealed class AgentEntry(NetworkStream controlStream)
    {
        public NetworkStream ControlStream { get; } = controlStream;
        public SemaphoreSlim SessionLock { get; } = new(1, 1);
        public TaskCompletionSource<NetworkStream>? PendingDataChannel;
    }
}
