using System.Text.Json;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SyncMesh.Contracts;

namespace SyncMesh.ServerHost.Nats;

// Publishes passive-monitoring telemetry for a server-tier node — the
// counterpart to SyncMesh.Daemon.Nats.MonitorPublisher. Plain core-NATS
// publish, no JetStream, for the same reason: current-state telemetry has
// nothing to replay. Self-describes this server's own configured mesh
// peers (ServerMeshOptions.Peers) and each connection's traffic count, so
// a mesh-wide monitor can build the whole topology from what every node
// says about itself. See docs/00-design-document.md §4.5 and ADR-0002's
// 2026-07-23 (Phase 3) Amendment.
public sealed class ServerMonitorPublisher(
    NatsConnection connection,
    ApplyResponder applyResponder,
    MeshForwarder meshForwarder,
    IOptions<ServerNatsOptions> natsOptions,
    IOptions<ServerMeshOptions> meshOptions,
    IOptions<ServerMonitorOptions> monitorOptions,
    ILogger<ServerMonitorPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = monitorOptions.Value;
        var subject = $"{opts.SubjectPrefix}.{opts.SiteId}.{opts.InstanceId}.status";

        using var timer = new PeriodicTimer(opts.PublishInterval);
        do
        {
            try
            {
                var forwardedCounts = meshForwarder.ForwardedCountsByPeerSiteId;
                var status = new ServerStatus
                {
                    SiteId = opts.SiteId,
                    InstanceId = opts.InstanceId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Url = natsOptions.Value.Url,
                    EventsAppliedCount = applyResponder.AppliedCount,
                    ConfiguredPeers = meshOptions.Value.Peers
                        .Select(peer => new PeerConnectionStatus
                        {
                            PeerSiteId = peer.SiteId,
                            PeerUrl = peer.Url,
                            EventsForwardedCount = forwardedCounts.GetValueOrDefault(peer.SiteId),
                        })
                        .ToList(),
                };

                await connection.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(status), cancellationToken: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same reasoning as MonitorPublisher: a missed tick isn't a
                // correctness problem, just a stale-until-next-tick display.
                logger.LogWarning(ex, "Failed to publish server monitor status; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
