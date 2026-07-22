using Microsoft.EntityFrameworkCore;
using SyncMesh.EventStore;

namespace SyncMesh.Daemon.Ipc;

// Buffered read path: serves what the local app has already recorded this
// session, entirely from the daemon's own local store. Never proxies to
// or from the server — see docs/00-design-document.md §4.1/§4.2.
public sealed class LocalEventReader(EventStoreDbContext db)
{
    public async Task<ReadEventsResponse> ReadAsync(ReadEventsRequest request, CancellationToken ct)
    {
        var events = await db.Events
            .Where(e => e.StreamId == request.StreamId)
            .OrderBy(e => e.StreamVersion)
            .Select(e => new RecordedEvent
            {
                GlobalEventId = e.GlobalEventId,
                StreamId = e.StreamId,
                StreamVersion = e.StreamVersion,
                EventType = e.EventType,
                PayloadJson = e.PayloadJson,
                PayloadSchemaVersion = e.PayloadSchemaVersion,
                RecordedAtUtc = e.RecordedAtUtc,
            })
            .ToListAsync(ct);

        return new ReadEventsResponse { Events = events };
    }
}
