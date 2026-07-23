using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SyncMesh.Daemon.Nats;

// Idempotently ensures the local WorkQueue stream + forwarder consumer
// exist on the daemon's embedded leaf node before anything tries to
// publish or consume. Ceiling defaults to unbounded except by available
// local disk (Discard=New on exhaustion) — see ADR-0002 Amendment
// 2026-07-22 and docs/00-design-document.md §4.2.
public sealed class DaemonJetStreamSetup(NatsJSContext jetStream, IOptions<DaemonNatsOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;

        await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(opts.StreamName, [$"{opts.SubjectPrefix}.>"])
            {
                Retention = StreamConfigRetention.Workqueue,
                Storage = StreamConfigStorage.File,
                MaxBytes = opts.MaxBytes,
                MaxMsgs = opts.MaxMsgs,
                MaxAge = opts.MaxAge,
                Discard = StreamConfigDiscard.New,
            },
            cancellationToken);

        await jetStream.CreateOrUpdateConsumerAsync(
            opts.StreamName,
            new ConsumerConfig(opts.ConsumerName)
            {
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            },
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
