using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace SyncMesh.Daemon.Nats;

// Pull-consumes the local WorkQueue stream and forwards each event to the
// hub as a plain core-NATS request, acking the JetStream message only once
// the hub confirms idempotent apply. Deliberately not JetStream stream
// mirroring — see ADR-0002 Amendment 2026-07-23.
public sealed class EventForwarder(
    NatsConnection connection,
    NatsJSContext jetStream,
    IOptions<DaemonNatsOptions> options,
    ILogger<EventForwarder> logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        // The pull-consume enumerable itself can throw (e.g. if a pull
        // request against the local stream times out or the connection
        // hiccups) — that must not silently kill the whole forwarder. Any
        // such fault restarts the consume loop rather than exiting
        // ExecuteAsync for good, which would otherwise strand every
        // buffered event un-acked forever, even after the hub recovers.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.GetConsumerAsync(opts.StreamName, opts.ConsumerName, stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        var reply = await connection.RequestAsync<byte[], byte[]>(
                            opts.ApplyRequestSubject,
                            msg.Data,
                            requestOpts: new NatsPubOpts(),
                            replyOpts: new NatsSubOpts { Timeout = opts.RequestTimeout },
                            cancellationToken: stoppingToken);

                        if (reply.Data is not null)
                        {
                            await msg.AckAsync(cancellationToken: stoppingToken);
                        }
                        else
                        {
                            logger.LogWarning("No reply payload from apply request; leaving message for redelivery.");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Nearest server unreachable or apply failed — leave
                        // the message un-acked. It stays in the local
                        // WorkQueue and is redelivered once reachable again.
                        logger.LogWarning(ex, "Failed to forward event to nearest server; will retry.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Forwarder consume loop faulted; restarting in {Delay}.", RestartDelay);
                await Task.Delay(RestartDelay, stoppingToken);
            }
        }
    }
}
