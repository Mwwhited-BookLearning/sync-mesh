using System.Text.Json;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;

namespace SyncMesh.Daemon.Nats;

// Publishes passive-monitoring telemetry on an ordinary NATS subject
// (monitor.<siteId>.<instanceId>.status) — plain core-NATS publish, no
// JetStream. Telemetry is current-state, not an event to replay: nothing
// here needs the durability/at-least-once machinery the event-sync path
// relies on, and this deliberately shares only the underlying NATS
// connection with that path, not its subjects, streams, or failure
// semantics. See docs/00-design-document.md §4.5.
public sealed class MonitorPublisher(
    NatsConnection connection,
    NatsJSContext jetStream,
    EventForwarder forwarder,
    IOptions<DaemonOptions> daemonOptions,
    IOptions<DaemonNatsOptions> natsOptions,
    IOptions<DaemonMonitorOptions> monitorOptions,
    ILogger<MonitorPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = monitorOptions.Value;
        var subject = $"{opts.SubjectPrefix}.{daemonOptions.Value.SiteId}.{daemonOptions.Value.InstanceId}.status";

        using var timer = new PeriodicTimer(opts.PublishInterval);
        do
        {
            try
            {
                var bufferedCount = await GetBufferedEventCountAsync(stoppingToken);
                var status = new DaemonStatus
                {
                    SiteId = daemonOptions.Value.SiteId,
                    InstanceId = daemonOptions.Value.InstanceId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    BufferedEventCount = bufferedCount,
                    ConnectedToNearestServer = connection.ConnectionState == NatsConnectionState.Open,
                    NearestServerUrl = natsOptions.Value.Url,
                    EventsForwardedCount = forwarder.ForwardedCount,
                };

                await connection.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(status), cancellationToken: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed telemetry tick is not a correctness problem —
                // unlike event forwarding, there is nothing to retry or
                // buffer here; the next tick just publishes fresh state.
                logger.LogWarning(ex, "Failed to publish monitor status; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<long> GetBufferedEventCountAsync(CancellationToken ct)
    {
        try
        {
            var info = await jetStream.GetStreamAsync(natsOptions.Value.StreamName, cancellationToken: ct);
            return (long)info.Info.State.Messages;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read local buffer depth for monitor status.");
            return -1;
        }
    }
}
