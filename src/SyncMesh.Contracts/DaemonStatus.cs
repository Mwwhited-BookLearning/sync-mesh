namespace SyncMesh.Contracts;

// Passive-monitoring telemetry — published as an ordinary NATS subject
// (monitor.<siteId>.<instanceId>.status), never via JetStream. This is
// current-state telemetry, not an event to replay: no durability guarantee
// is needed or wanted here, unlike EventEnvelope. See
// docs/00-design-document.md §4.5 and docs/06-data-model.md §5.
public sealed class DaemonStatus
{
    // Discriminator for a mesh-wide monitor subscribing to monitor.> and
    // seeing both DaemonStatus and ServerStatus messages mixed together —
    // see SyncMesh.Contracts.ServerStatus.
    public string NodeKind => "daemon";

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

    // This daemon's own configured nearest-server URL
    // (DaemonNatsOptions.Url) — self-reported topology, not something a
    // monitor guesses at from outside. Empty/null-ish values are valid:
    // "client isolated" deployments (§4.3) have nothing reachable here.
    public string NearestServerUrl { get; init; } = default!;

    // Count of events this daemon has successfully forwarded to (and had
    // acked by) its nearest server — this connection's own traffic count.
    public long EventsForwardedCount { get; init; }
}
