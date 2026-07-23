using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SyncMesh.Contracts;
using SyncMesh.EventStore;

namespace SyncMesh.ServerHost.Nats;

// Core-NATS request/reply responder on the hub side. Deliberately does not
// use JetStream cross-leaf mirroring (see ADR-0002 Amendment 2026-07-23) —
// plain request/reply already crosses the leaf-node boundary transparently.
public sealed class ApplyResponder(
    IOptions<ServerNatsOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<ApplyResponder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = options.Value.Url });

        await foreach (var msg in connection.SubscribeAsync<byte[]>(options.Value.ApplyRequestSubject, cancellationToken: stoppingToken))
        {
            try
            {
                await ApplyAsync(msg.Data);

                if (msg.ReplyTo is not null)
                {
                    await connection.PublishAsync(msg.ReplyTo, "ok"u8.ToArray(), cancellationToken: stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error applying incoming event.");

                if (msg.ReplyTo is not null)
                {
                    await connection.PublishAsync(msg.ReplyTo, "error"u8.ToArray(), cancellationToken: stoppingToken);
                }
            }
        }
    }

    private async Task ApplyAsync(byte[]? payload)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(payload!)
            ?? throw new InvalidOperationException("Empty EventEnvelope payload.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        // Idempotent apply: dedupe by GlobalEventId before inserting.
        // See docs/06-data-model.md §4.
        var alreadyApplied = await db.Events.AnyAsync(e => e.GlobalEventId == envelope.GlobalEventId);
        if (alreadyApplied)
        {
            return;
        }

        db.Events.Add(new EventRecord
        {
            GlobalEventId = envelope.GlobalEventId,
            StreamId = envelope.StreamId,
            StreamVersion = envelope.StreamVersion,
            OriginSiteId = envelope.OriginSiteId,
            HlcPhysicalTicks = envelope.Hlc.PhysicalTicks,
            HlcLogicalCounter = envelope.Hlc.LogicalCounter,
            RecordedAtUtc = envelope.RecordedAtUtc,
            EventType = envelope.EventType,
            PayloadJson = envelope.PayloadJson,
            PayloadSchemaVersion = envelope.PayloadSchemaVersion,
        });

        await db.SaveChangesAsync();
    }
}
