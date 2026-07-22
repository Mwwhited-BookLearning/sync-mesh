namespace SyncMesh.Contracts;

// Common envelope wrapping every event at every tier. This is the contract
// that makes cross-site idempotency and ordering possible — do not let
// tier-specific code invent parallel shapes. See docs/06-data-model.md §1.
public sealed class EventEnvelope
{
    // Global, immutable, unique identifier for this exact event.
    // Used for idempotent apply / dedupe across every consumer.
    public required Guid GlobalEventId { get; init; }

    // Aggregate identity within the originating site.
    public required Guid StreamId { get; init; }

    // Local, per-stream, monotonically increasing version at the
    // originating site. Used for optimistic concurrency at that site only.
    public required long StreamVersion { get; init; }

    // Which daemon/server first recorded this event. Combined with
    // StreamId + StreamVersion, gives you a natural composite key as an
    // alternative to GlobalEventId if preferred.
    public required string OriginSiteId { get; init; }

    // Hybrid Logical Clock value assigned at the originating site.
    // Authoritative for cross-site ordering. See HybridLogicalClock.
    public required HybridLogicalClock Hlc { get; init; }

    // Wall-clock capture time. Informational / diagnostic only.
    // NEVER use this for authoritative ordering decisions.
    public required DateTimeOffset RecordedAtUtc { get; init; }

    // Discriminator for polymorphic payload handling.
    public required string EventType { get; init; }

    // Serialized event payload (JSON recommended for portability and
    // human-readability during debugging).
    public required string PayloadJson { get; init; }

    // Schema/version tag for the payload shape, to support safe evolution.
    public required int PayloadSchemaVersion { get; init; }
}
