using System.Text.Json;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SyncMesh.Contracts;

namespace SyncMesh.Daemon.Tunnel;

// Publishes TunnelStatus telemetry on the already-reserved
// tunnel.<siteId>.<instanceId>.control subject — plain core-NATS publish,
// no JetStream, structurally identical to MonitorPublisher. Current-state
// only: nothing here needs replaying, so a missed tick is superseded by
// the next one. Real tunnel session-establishment signaling happens
// entirely inside TunnelAgent's plain-TCP mechanism and never touches
// NATS — see docs/06-data-model.md §6 and
// docs/adr/0007-custom-reverse-tunnel-mechanism.md.
public sealed class TunnelStatusPublisher(
    NatsConnection connection,
    TunnelAgent tunnelAgent,
    IOptions<DaemonOptions> daemonOptions,
    IOptions<TunnelAgentOptions> tunnelOptions,
    ILogger<TunnelStatusPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = tunnelOptions.Value;
        var subject = $"{opts.SubjectPrefix}.{daemonOptions.Value.SiteId}.{daemonOptions.Value.InstanceId}.control";

        using var timer = new PeriodicTimer(opts.StatusPublishInterval);
        do
        {
            try
            {
                var status = new TunnelStatus
                {
                    SiteId = daemonOptions.Value.SiteId,
                    InstanceId = daemonOptions.Value.InstanceId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ConnectedToRelay = tunnelAgent.ConnectedToRelay,
                    RelayUrl = opts.RelayUrl,
                    SessionActive = tunnelAgent.SessionActive,
                };

                await connection.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(status), cancellationToken: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same reasoning as MonitorPublisher: a missed tick isn't a
                // correctness problem, just a stale-until-next-tick display.
                logger.LogWarning(ex, "Failed to publish tunnel status; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
