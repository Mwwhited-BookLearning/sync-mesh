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
// than silently accepting an event the leaf node never buffered.
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
                await jetStream.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(envelope), cancellationToken: ct);

                return new AppendEventResponse
                {
                    GlobalEventId = record.GlobalEventId,
                    StreamVersion = record.StreamVersion,
                    HlcPhysicalTicks = record.HlcPhysicalTicks,
                    HlcLogicalCounter = record.HlcLogicalCounter,
                    RecordedAtUtc = record.RecordedAtUtc,
                };
            }
            catch (DbUpdateException) when (attempt < MaxConcurrencyRetries)
            {
                db.Entry(record).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"Failed to append to stream '{request.StreamId}' after {MaxConcurrencyRetries} concurrency retries.");
    }
}
