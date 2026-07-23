using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon.Nats;
using SyncMesh.EventStore;

namespace SyncMesh.Daemon.Ipc;

// Append-only write path: assigns GlobalEventId + HLC + the next
// StreamVersion, persists via EF Core, then publishes to the local
// JetStream WorkQueue stream (publish-on-write — see
// docs/05-implementation-guide.md Phase 2). The (StreamId, StreamVersion)
// unique index (see SyncMesh.EventStore.EventStoreDbContext) is the
// optimistic-concurrency guard — a conflicting concurrent write fails the
// unique constraint and is retried with a freshly computed version.
//
// The local NATS leaf node is core daemon infrastructure, not an optional
// extra — if the JetStream publish fails, the whole append fails rather
// than silently accepting an event the leaf node never buffered. Since the
// SQLite save and the JetStream publish can't share one transaction, a
// publish failure after a successful save is compensated by deleting the
// row (see below) rather than left orphaned — an orphaned row would never
// sync and would permanently block this stream's version sequence.
public sealed class LocalEventWriter(
    EventStoreDbContext db,
    HlcGenerator hlcGenerator,
    IOptions<DaemonOptions> daemonOptions,
    NatsJSContext jetStream,
    IOptions<DaemonNatsOptions> natsOptions)
{
    private const int MaxConcurrencyRetries = 5;

    public async Task<AppendEventResponse> AppendAsync(AppendEventRequest request, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            var nextVersion = 1 + await db.Events
                .Where(e => e.StreamId == request.StreamId)
                .Select(e => (long?)e.StreamVersion)
                .MaxAsync(ct) ?? 1;

            var hlc = hlcGenerator.Next();
            var record = new EventRecord
            {
                GlobalEventId = Guid.NewGuid(),
                StreamId = request.StreamId,
                StreamVersion = nextVersion,
                OriginSiteId = daemonOptions.Value.SiteId,
                HlcPhysicalTicks = hlc.PhysicalTicks,
                HlcLogicalCounter = hlc.LogicalCounter,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                EventType = request.EventType,
                PayloadJson = request.PayloadJson,
                PayloadSchemaVersion = request.PayloadSchemaVersion,
            };

            db.Events.Add(record);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt < MaxConcurrencyRetries)
            {
                db.Entry(record).State = EntityState.Detached;
                continue;
            }

            var envelope = new EventEnvelope
            {
                GlobalEventId = record.GlobalEventId,
                StreamId = record.StreamId,
                StreamVersion = record.StreamVersion,
                OriginSiteId = record.OriginSiteId,
                Hlc = hlc,
                RecordedAtUtc = record.RecordedAtUtc,
                EventType = record.EventType,
                PayloadJson = record.PayloadJson,
                PayloadSchemaVersion = record.PayloadSchemaVersion,
            };
            var subject = $"{natsOptions.Value.SubjectPrefix}.{record.OriginSiteId}.{record.StreamId}";

            try
            {
                // PublishAsync does not throw on a server-side rejection
                // (e.g. the stream's configured cap is full) by itself —
                // it returns a PubAckResponse that must be checked.
                // EnsureSuccess() is what actually throws in that case.
                var ack = await jetStream.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(envelope), cancellationToken: ct);
                ack.EnsureSuccess();
            }
            catch (Exception publishEx) when (publishEx is not OperationCanceledException)
            {
                // Compensate: the row is committed but was never buffered
                // for forwarding (e.g. the local buffer's configured
                // capacity cap was hit — Discard: New rejects the
                // publish). Deleting it, rather than leaving it orphaned,
                // lets the caller retry cleanly and get a correctly
                // sequenced version instead of a permanent gap.
                db.Events.Remove(record);
                await db.SaveChangesAsync(CancellationToken.None);

                throw new InvalidOperationException(
                    "Event was durably stored locally but could not be buffered for forwarding " +
                    "(local NATS leaf node publish failed — see inner exception). The write has " +
                    "been rolled back; the caller should retry.",
                    publishEx);
            }

            return new AppendEventResponse
            {
                GlobalEventId = record.GlobalEventId,
                StreamVersion = record.StreamVersion,
                HlcPhysicalTicks = record.HlcPhysicalTicks,
                HlcLogicalCounter = record.HlcLogicalCounter,
                RecordedAtUtc = record.RecordedAtUtc,
            };
        }

        throw new InvalidOperationException(
            $"Failed to append to stream '{request.StreamId}' after {MaxConcurrencyRetries} concurrency retries.");
    }
}
