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

## 6. Monitoring Telemetry Payload Shapes

Published to the `monitor.*` subjects above (§5), current-state only — no
event envelope, no HLC, nothing to replay. Consumed today by
`SyncMesh.MonitorClient` (per-instance CLI, Phase 4) and
`SyncMesh.MeshMonitor.Api` (mesh-wide dashboard, ADR-0005).

**`DaemonStatus`** (`SyncMesh.Contracts.DaemonStatus`) — self-reported by
a local daemon:

| Field | Meaning |
|---|---|
| `SiteId` / `InstanceId` | Identifies which daemon this is (`InstanceId` defaults to machine name) |
| `TimestampUtc` | When this snapshot was taken |
| `BufferedEventCount` | Events in the local JetStream WorkQueue stream not yet acked by the nearest server |
| `LeafConnected` | Whether the embedded leaf node currently has a live connection to its nearest server |

**`ServerStatus`** (`SyncMesh.Contracts.ServerStatus`) — self-reported by
a server-tier node; the counterpart to `DaemonStatus`:

| Field | Meaning |
|---|---|
| `SiteId` / `InstanceId` | Identifies which server this is |
| `TimestampUtc` | When this snapshot was taken |
| `Url` | This server's own listening/apply-endpoint URL, so a daemon's nearest-server edge can be matched to this node |
| `EventsAppliedCount` | Events durably applied here for the first time (from daemons and/or peers combined) |
| `ConfiguredPeers` | This server's own configured mesh peers (`ServerMeshOptions.Peers`) plus a per-peer forwarded-event count; empty for a standalone server (§4.4) |

Both shapes self-describe their own relationships (a daemon's nearest
server, a server's configured peers) rather than requiring a
separately-maintained topology file that could drift out of sync with
actual configuration — this is what lets `SyncMesh.MeshMonitor.Api` build
a whole mesh's topology purely from what every node says about itself.

## 7. Event Lineage (Descriptive Provenance)

See ADR-0006. An event can descriptively reference the prior event(s) its
data was derived/sourced from — genuinely many-to-many (multiple parents,
multiple children) — purely for audit/traceability. This is **never** an
ordering or dependency mechanism: HLC ordering (§3) and idempotent apply
(§4) work exactly as before, unaffected by lineage.

`EventLineage` (`SyncMesh.EventStore.EventLineage`):

| Field | Meaning |
|---|---|
| `ChildEventId` | The event whose data was derived from `ParentEventId` |
| `ParentEventId` | The prior event `ChildEventId`'s data was sourced from |

Composite primary key `(ChildEventId, ParentEventId)` — no duplicate pair,
plus a secondary index on `ParentEventId` for "children of a given parent"
lookups. **Deliberately no database-enforced foreign key** to
`EventRecord.GlobalEventId` in either direction: apply must stay safe
under out-of-order, at-least-once delivery, and a child event can
legitimately be applied before its parent arrives via a different mesh
path. A hard FK would reject that child's apply purely due to a benign
timing race — see ADR-0006 for the full reasoning.

Referential integrity is instead enforced only at authorship time:
`LocalEventWriter` validates that every requested `ParentEventId` already
exists in *that daemon's own* local `EventStoreDbContext` before writing —
safe today because a daemon's `Events` table is never purged on ack (only
the JetStream WorkQueue message is).

`EventEnvelope.ParentEventIds` (optional, defaults empty) carries this
from the daemon's write path through to the server's apply path
unchanged — `ApplyResponder` inserts the corresponding `EventLineage` rows
alongside the `EventRecord` insert, gated by the exact same
already-applied idempotency check that protects `EventRecord` itself, so
lineage rows can never be double-inserted for the same event.
