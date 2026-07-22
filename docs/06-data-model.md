# Data Model

## 1. Event Envelope

Every event, at every tier, is wrapped in a common envelope. This is the
contract that makes cross-site idempotency and ordering possible — do not
let tier-specific code invent parallel shapes.

```csharp
public sealed class EventEnvelope
{
    // Global, immutable, unique identifier for this exact event.
    // Used for idempotent apply / dedupe across every consumer.
    public Guid GlobalEventId { get; init; }

    // Aggregate identity within the originating site.
    public Guid StreamId { get; init; }

    // Local, per-stream, monotonically increasing version at the
    // originating site. Used for optimistic concurrency at that site only.
    public long StreamVersion { get; init; }

    // Which daemon/server first recorded this event. Combined with
    // StreamId + StreamVersion, gives you a natural composite key as an
    // alternative to GlobalEventId if preferred.
    public string OriginSiteId { get; init; } = default!;

    // Hybrid Logical Clock value assigned at the originating site.
    // Authoritative for cross-site ordering. See section 3.
    public HybridLogicalClock Hlc { get; init; }

    // Wall-clock capture time. Informational / diagnostic only.
    // NEVER use this for authoritative ordering decisions.
    public DateTimeOffset RecordedAtUtc { get; init; }

    // Discriminator for polymorphic payload handling.
    public string EventType { get; init; } = default!;

    // Serialized event payload (JSON recommended for portability and
    // human-readability during debugging).
    public string PayloadJson { get; init; } = default!;

    // Schema/version tag for the payload shape, to support safe evolution.
    public int PayloadSchemaVersion { get; init; }
}
```

## 2. EF Core Entity + Table Shape

Kept intentionally simple — this is a table per aggregate-hierarchy
pattern, portable across SQLite, PostgreSQL, and SQL Server without
provider-specific SQL.

```csharp
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
```

```csharp
public class EventStoreDbContext : DbContext
{
    public DbSet<EventRecord> Events => Set<EventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>(e =>
        {
            e.HasKey(x => x.GlobalEventId);

            // Enforce append-only, gapless versioning per stream at the
            // originating site. This is the concurrency guard.
            e.HasIndex(x => new { x.StreamId, x.StreamVersion }).IsUnique();

            // Support efficient "give me everything since HLC X" queries
            // for sync.
            e.HasIndex(x => new { x.HlcPhysicalTicks, x.HlcLogicalCounter });

            e.Property(x => x.OriginSiteId).HasMaxLength(128);
            e.Property(x => x.EventType).HasMaxLength(256);
        });
    }
}
```

Provider selection stays entirely in DI configuration
(`UseSqlite` / `UseNpgsql` / `UseSqlServer`) — no code above should ever
need to know which provider is active.

## 3. Hybrid Logical Clock (HLC)

Minimal HLC implementation sketch — combines wall-clock time with a
logical counter so that causally-related events across sites can be
ordered deterministically, without requiring synchronized clocks.

```csharp
public readonly record struct HybridLogicalClock(long PhysicalTicks, int LogicalCounter)
    : IComparable<HybridLogicalClock>
{
    public int CompareTo(HybridLogicalClock other)
    {
        var physical = PhysicalTicks.CompareTo(other.PhysicalTicks);
        return physical != 0 ? physical : LogicalCounter.CompareTo(other.LogicalCounter);
    }
}

public sealed class HlcGenerator
{
    private long _lastPhysical;
    private int _counter;
    private readonly object _lock = new();

    public HybridLogicalClock Next()
    {
        lock (_lock)
        {
            var physicalNow = DateTimeOffset.UtcNow.UtcTicks;
            if (physicalNow > _lastPhysical)
            {
                _lastPhysical = physicalNow;
                _counter = 0;
            }
            else
            {
                _counter++;
            }
            return new HybridLogicalClock(_lastPhysical, _counter);
        }
    }

    // Call this when receiving an event from another site, to fold its
    // clock into ours and preserve causal ordering going forward.
    public HybridLogicalClock Merge(HybridLogicalClock received)
    {
        lock (_lock)
        {
            var physicalNow = DateTimeOffset.UtcNow.UtcTicks;
            var maxPhysical = Math.Max(physicalNow, Math.Max(_lastPhysical, received.PhysicalTicks));

            if (maxPhysical == _lastPhysical && maxPhysical == received.PhysicalTicks)
                _counter = Math.Max(_counter, received.LogicalCounter) + 1;
            else if (maxPhysical == _lastPhysical)
                _counter++;
            else if (maxPhysical == received.PhysicalTicks)
                _counter = received.LogicalCounter + 1;
            else
                _counter = 0;

            _lastPhysical = maxPhysical;
            return new HybridLogicalClock(_lastPhysical, _counter);
        }
    }
}
```

This is a starting point, not a drop-in production library — validate
clock-skew handling and counter overflow behavior during implementation,
and write BDD scenarios for the specific edge cases you find (see
`docs/bdd/features/event-ordering-and-idempotency.feature`).

## 4. Idempotent Apply — Reference Shape

```csharp
public async Task ApplyIncomingEventAsync(EventEnvelope incoming, CancellationToken ct)
{
    var alreadyApplied = await _db.Events
        .AnyAsync(e => e.GlobalEventId == incoming.GlobalEventId, ct);

    if (alreadyApplied)
        return; // safe no-op; transport guarantees at-least-once, not exactly-once

    _db.Events.Add(MapToRecord(incoming));
    await _db.SaveChangesAsync(ct);

    _hlcGenerator.Merge(incoming.Hlc);
}
```

## 5. NATS Subject Naming Convention

| Purpose | Subject pattern |
|---|---|
| Event sync (daemon → server, server → server) | `events.<originSiteId>.<streamId>` |
| Monitoring/telemetry | `monitor.<siteId>.<instanceId>.<metric>` |
| Tunnel signaling (not the tunnel data itself) | `tunnel.<siteId>.<instanceId>.control` |

Keep monitoring and tunnel subjects namespaced separately from
`events.*` so permissions, retention, and failure isolation can be
configured independently per ADR-0004.
