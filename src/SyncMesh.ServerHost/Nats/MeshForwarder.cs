using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace SyncMesh.ServerHost.Nats;

// Generalizes SyncMesh.Daemon.Nats.EventForwarder from "one nearest server"
// to "N configured peers": one independent pull-consume + forward + ack
// loop per peer, each using its own point-to-point NatsConnection directly
// to that peer's URL (not a shared gateway-routed connection). See
// docs/adr/0002-nats-leaf-nodes-for-transport.md's 2026-07-23 (Phase 3)
// Amendment.
public sealed class MeshForwarder(
    NatsJSContext jetStream,
    IOptions<ServerMeshOptions> options,
    ILogger<MeshForwarder> logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var peers = options.Value.Peers;
        await Task.WhenAll(peers.Select(peer => ForwardToPeerAsync(peer, stoppingToken)));
    }

    private async Task ForwardToPeerAsync(MeshPeerOptions peer, CancellationToken stoppingToken)
    {
        var streamName = options.Value.OutboundStreamName;
        var consumerName = ServerMeshSetup.ConsumerNameFor(peer);
        var requestTimeout = options.Value.RequestTimeout;

        await using var peerConnection = new NatsConnection(new NatsOpts { Url = peer.Url });

        // Same outer retry loop as EventForwarder, for the same reason: a
        // BackgroundService that exits early on an unhandled fault would
        // silently strand this peer's un-acked backlog forever, even after
        // the peer becomes reachable again.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.GetConsumerAsync(streamName, consumerName, stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        var reply = await peerConnection.RequestAsync<byte[], byte[]>(
                            peer.ApplyRequestSubject,
                            msg.Data,
                            requestOpts: new NatsPubOpts(),
                            replyOpts: new NatsSubOpts { Timeout = requestTimeout },
                            cancellationToken: stoppingToken);

                        if (reply.Data is not null)
                        {
                            await msg.AckAsync(cancellationToken: stoppingToken);
                        }
                        else
                        {
                            logger.LogWarning("No reply payload forwarding to peer {PeerSiteId}; leaving message for redelivery.", peer.SiteId);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Peer unreachable or apply failed — leave the
                        // message un-acked. It stays in MESH_OUTBOUND (this
                        // peer's interest keeps it retained) and is
                        // redelivered once the peer is reachable again.
                        logger.LogWarning(ex, "Failed to forward event to peer {PeerSiteId}; will retry.", peer.SiteId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Mesh forwarder loop for peer {PeerSiteId} faulted; restarting in {Delay}.", peer.SiteId, RestartDelay);
                await Task.Delay(RestartDelay, stoppingToken);
            }
        }
    }
}
