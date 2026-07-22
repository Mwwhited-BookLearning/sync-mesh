using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts;
using SyncMesh.EventStore;

namespace SyncMesh.Daemon.Ipc;

// Append-only write path: assigns GlobalEventId + HLC + the next
// StreamVersion, then persists via EF Core. The (StreamId, StreamVersion)
// unique index (see SyncMesh.EventStore.EventStoreDbContext) is the
// optimistic-concurrency guard — a conflicting concurrent write fails the
// unique constraint and is retried with a freshly computed version.
public sealed class LocalEventWriter(
    EventStoreDbContext db,
    HlcGenerator hlcGenerator,
    IOptions<DaemonOptions> daemonOptions)
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
