namespace SyncMesh.Contracts;

// Passive-monitoring telemetry for a server-tier node — the counterpart to
// DaemonStatus. Published as an ordinary NATS subject
// (monitor.<siteId>.<instanceId>.status), never via JetStream, for the same
// reason: current-state, not a replayable event. Self-describes this
// server's own configured mesh peers so a mesh-wide monitor can build the
// whole topology from what every node says about itself, rather than from
// a separately-maintained config file that can drift out of sync.
public sealed class ServerStatus
{
    public string NodeKind => "server";

    public string SiteId { get; init; } = default!;
    public string InstanceId { get; init; } = default!;
    public DateTimeOffset TimestampUtc { get; init; }

    // This server's own listening/apply-endpoint URL (ServerNatsOptions
    // .Url) — self-reported so a mesh-wide monitor can match a daemon's
    // NearestServerUrl to this specific node and draw the daemon→server
    // edge, the same way ConfiguredPeers lets it draw server↔server edges.
    public string Url { get; init; } = default!;

    // Count of events durably applied here for the first time (from
    // daemons and/or peers combined) — see ApplyResponder.AppliedCount.
    public long EventsAppliedCount { get; init; }

    // This server's own configured mesh peers (ServerMeshOptions.Peers)
    // and per-peer forwarded-event counts — empty for a standalone server
    // (§4.4's "first-class, permanent deployment mode").
    public List<PeerConnectionStatus> ConfiguredPeers { get; init; } = [];
}

public sealed class PeerConnectionStatus
{
    public string PeerSiteId { get; init; } = default!;
    public string PeerUrl { get; init; } = default!;
    public long EventsForwardedCount { get; init; }
}
