using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.EventStore;

namespace SyncMesh.ServerHost.Nats;

// Core-NATS request/reply responder — shared by both daemons (nearest-server
// hop, §4.2) and mesh peers (server-mesh hop, §4.4): both call the exact
// same ApplyRequestSubject with the same EventEnvelope shape, and both get
// idempotent-apply-then-ack. Deliberately does not use JetStream cross-leaf
// mirroring (see ADR-0002 Amendment 2026-07-23) — plain request/reply
// already crosses the leaf-node boundary transparently.
public sealed class ApplyResponder(
    NatsConnection connection,
    NatsJSContext jetStream,
    IOptions<ServerNatsOptions> options,
    IOptions<ServerMeshOptions> meshOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<ApplyResponder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in connection.SubscribeAsync<byte[]>(options.Value.ApplyRequestSubject, cancellationToken: stoppingToken))
        {
            try
            {
                await ApplyAsync(msg.Data, stoppingToken);

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

    private async Task ApplyAsync(byte[]? payload, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(payload!)
            ?? throw new InvalidOperationException("Empty EventEnvelope payload.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        // Idempotent apply: dedupe by GlobalEventId before inserting. A
        // duplicate can arrive legitimately from two directions now — a
        // daemon retry, or a mesh peer gossiping back an event that
        // reached this server by another path first — see docs/06-data-model.md §4.
        var alreadyApplied = await db.Events.AnyAsync(e => e.GlobalEventId == envelope.GlobalEventId, ct);
        if (alreadyApplied)
        {
            return;
        }

        var record = new EventRecord
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
        };
        db.Events.Add(record);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;

            // Two concurrent requests delivering the same GlobalEventId
            // (e.g. a daemon's direct write racing a peer's gossiped copy
            // of the same event) can both pass the AnyAsync check above
            // before either commits. The unique GlobalEventId primary key
            // makes the loser's insert fail — that's the duplicate-delivery
            // no-op arriving via a race instead of a retry, so only treat
            // it as safe if THIS GlobalEventId is what's actually present
            // now. Any other unique-constraint violation (e.g. two
            // different events colliding on (StreamId, StreamVersion)) is a
            // genuine data-integrity problem, not a duplicate — surface it
            // rather than silently swallowing it.
            var appliedByRace = await db.Events.AnyAsync(e => e.GlobalEventId == envelope.GlobalEventId, ct);
            if (appliedByRace)
            {
                return;
            }

            throw;
        }

        // Relay to this server's own peers only if it genuinely didn't
        // exist yet here (skipped on the duplicate/no-op paths above) —
        // this is what stops gossip amplification: an event bounces back
        // to its origin at most once, since the origin's own no-op path
        // never re-publishes. Skipped entirely with no configured peers
        // (a standalone server has nothing to relay to) — see ADR-0002's
        // 2026-07-23 (Phase 3) Amendment.
        if (meshOptions.Value.Peers.Count == 0)
        {
            return;
        }

        var subject = $"{meshOptions.Value.OutboundSubjectPrefix}.{envelope.OriginSiteId}.{envelope.StreamId}";

        try
        {
            var ack = await jetStream.PublishAsync(subject, payload, cancellationToken: ct);
            ack.EnsureSuccess();
        }
        catch (Exception publishEx) when (publishEx is not OperationCanceledException)
        {
            // Same compensating-delete pattern as LocalEventWriter: this
            // server's own durability contract and the mesh's convergence
            // contract are treated as one atomic unit from the caller's
            // point of view. Roll back so the caller retries the whole
            // apply-and-relay operation rather than being told "ok" while
            // the event silently never reaches this server's peers.
            db.Events.Remove(record);
            await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException(
                "Event was durably applied locally but could not be relayed to this " +
                "server's mesh peers (local MESH_OUTBOUND publish failed — see inner " +
                "exception). The apply has been rolled back; the caller should retry.",
                publishEx);
        }
    }
}
