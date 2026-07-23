using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SyncMesh.Contracts;

namespace SyncMesh.MeshMonitor.Api;

// Subscribes to monitor.> — the same passive-monitoring subject namespace
// SyncMesh.Daemon.Nats.MonitorPublisher and SyncMesh.ServerHost.Nats
// .ServerMonitorPublisher publish to — and turns each self-reported
// DaemonStatus/ServerStatus into a live topology view: an in-memory
// snapshot (ITopologyStore, for freshly-opened browser tabs) plus a
// SignalR push to already-connected ones. Plain core-NATS subscribe, no
// JetStream, matching the telemetry's own current-state-only contract.
public sealed class MonitorSubscriber(
    IOptions<MeshMonitorApiOptions> options,
    ITopologyStore store,
    IHubContext<MeshMonitorHub> hub,
    ILogger<MonitorSubscriber> logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = options.Value.NatsUrl });

        // Same outer retry loop convention as EventForwarder/MeshForwarder
        // — a subscribe fault must not silently end monitoring for good.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var msg in connection.SubscribeAsync<byte[]>("monitor.>", cancellationToken: stoppingToken))
                {
                    try
                    {
                        var node = ParseNode(msg.Data);
                        if (node is null)
                        {
                            continue;
                        }

                        store.Upsert(node);
                        await hub.Clients.All.SendAsync("NodeUpdated", node, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Failed to process a monitor message; skipping it.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Monitor subscriber faulted; restarting in {Delay}.", RestartDelay);
                await Task.Delay(RestartDelay, stoppingToken);
            }
        }
    }

    private static TopologyNode? ParseNode(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("NodeKind", out var kindProperty))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        return kindProperty.GetString() switch
        {
            "daemon" => Map(JsonSerializer.Deserialize<DaemonStatus>(data), s => new TopologyNode("daemon", s.SiteId, s.InstanceId, now, s)),
            "server" => Map(JsonSerializer.Deserialize<ServerStatus>(data), s => new TopologyNode("server", s.SiteId, s.InstanceId, now, s)),
            _ => null,
        };
    }

    private static TopologyNode? Map<T>(T? status, Func<T, TopologyNode> project) where T : class =>
        status is null ? null : project(status);
}
