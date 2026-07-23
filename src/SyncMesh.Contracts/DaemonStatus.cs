namespace SyncMesh.Contracts;

// Passive-monitoring telemetry — published as an ordinary NATS subject
// (monitor.<siteId>.<instanceId>.status), never via JetStream. This is
// current-state telemetry, not an event to replay: no durability guarantee
// is needed or wanted here, unlike EventEnvelope. See
// docs/00-design-document.md §4.5 and docs/06-data-model.md §5.
public sealed class DaemonStatus
{
    public string SiteId { get; init; } = default!;
    public string InstanceId { get; init; } = default!;
    public DateTimeOffset TimestampUtc { get; init; }

    // Count of events still sitting in the local JetStream buffer,
    // unacknowledged by the nearest server — the same number
    // local-durability.feature exercises, surfaced here for observability.
    public long BufferedEventCount { get; init; }

    // Whether the daemon's local leaf node currently has an open
    // connection. Reflects the leaf's own link state, not a deeper
    // application-level handshake with the nearest server.
    public bool ConnectedToNearestServer { get; init; }
}
