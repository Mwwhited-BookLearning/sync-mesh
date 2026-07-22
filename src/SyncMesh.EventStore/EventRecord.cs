namespace SyncMesh.EventStore;

// Table-per-hierarchy event row, portable across SQLite, PostgreSQL, and
// SQL Server without provider-specific SQL. See docs/06-data-model.md §2.
public class EventRecord
{
    public Guid GlobalEventId { get; set; }
    public Guid StreamId { get; set; }
    public long StreamVersion { get; set; }
    public string OriginSiteId { get; set; } = default!;

    // Store HLC as two columns: physical time (ticks) + logical counter.
    // Keeps it queryable/sortable without a custom comparer in SQL.
    public long HlcPhysicalTicks { get; set; }
    public int HlcLogicalCounter { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
    public string EventType { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;
    public int PayloadSchemaVersion { get; set; }
}
