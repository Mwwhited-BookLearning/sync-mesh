using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SyncMesh.ServerHost.Nats;

// Idempotently ensures the server's own MESH_OUTBOUND relay stream, plus one
// durable consumer per configured peer, exist before ApplyResponder starts
// relaying or MeshForwarder starts forwarding. Interest retention needs a
// consumer to already exist for a message to have "interest" at all, so
// consumers must be provisioned up front, the same reasoning as
// DaemonJetStreamSetup's stream+consumer pairing. See
// docs/adr/0002-nats-leaf-nodes-for-transport.md's 2026-07-23 (Phase 3)
// Amendment.
public sealed class ServerMeshSetup(NatsJSContext jetStream, IOptions<ServerMeshOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;

        await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(opts.OutboundStreamName, [$"{opts.OutboundSubjectPrefix}.>"])
            {
                // Interest, not WorkQueue: every configured peer needs its
                // own copy of each event. WorkQueue removes a message once
                // ANY single consumer acks it, which would silently drop it
                // for every other peer still waiting.
                Retention = StreamConfigRetention.Interest,
                Storage = StreamConfigStorage.File,
                MaxBytes = opts.MaxBytes,
                MaxMsgs = opts.MaxMsgs,
                MaxAge = opts.MaxAge,
                Discard = StreamConfigDiscard.New,
            },
            cancellationToken);

        foreach (var peer in opts.Peers)
        {
            await jetStream.CreateOrUpdateConsumerAsync(
                opts.OutboundStreamName,
                new ConsumerConfig(ConsumerNameFor(peer))
                {
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                    AckWait = opts.AckWait,
                },
                cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string ConsumerNameFor(MeshPeerOptions peer) => $"TO_{peer.SiteId}";
}
