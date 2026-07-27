namespace SyncMesh.EventStore;

// Descriptive-only provenance: which prior event(s) a given event's data
// was derived/sourced from. Genuine many-to-many (multiple parents,
// multiple children) — purely for traceability/audit. Deliberately NOT a
// DB-enforced foreign key to EventRecord.GlobalEventId: apply must stay
// safe against out-of-order/at-least-once delivery — a child can
// legitimately be applied at a given node before its parent arrives via a
// different gossip path (ApplyResponder is a shared endpoint for both
// daemon-forwarded and peer-gossiped events with no cross-node ordering
// guarantee). A hard FK would reject that child's apply purely due to a
// benign timing race. Referential integrity is enforced only at
// authorship time (see LocalEventWriter), never at apply time. Never
// affects idempotent-apply, HLC ordering, or replay. See
// docs/06-data-model.md §7, ADR-0006.
public class EventLineage
{
    public Guid ChildEventId { get; set; }
    public Guid ParentEventId { get; set; }
}
