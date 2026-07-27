using Microsoft.EntityFrameworkCore;

namespace SyncMesh.EventStore;

public class EventStoreDbContext(DbContextOptions<EventStoreDbContext> options) : DbContext(options)
{
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<EventLineage> EventLineages => Set<EventLineage>();

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

        // Descriptive-only provenance graph — see EventLineage.cs. No FK to
        // Events by design; see docs/adr/0006-event-lineage-descriptive-provenance.md.
        modelBuilder.Entity<EventLineage>(e =>
        {
            e.ToTable("EventLineage");
            e.HasKey(x => new { x.ChildEventId, x.ParentEventId });
            e.HasIndex(x => x.ParentEventId);
        });
    }
}
