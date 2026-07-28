namespace SyncMesh.Contracts;

// Passive-monitoring telemetry for the daemon's tunnel agent — current-
// state only, published on the already-reserved
// monitor.<siteId>.<instanceId>.control subject (see docs/06-data-model.md
// §5/§6). Despite the "control" name, this is pure telemetry: real
// session-establishment signaling (Hello/OpenDataChannel/etc.) happens
// entirely inside the plain-TCP tunnel mechanism and never touches NATS —
// see docs/adr/0007-custom-reverse-tunnel-mechanism.md.
public sealed class TunnelStatus
{
    public string NodeKind => "daemon";

    public string SiteId { get; init; } = default!;
    public string InstanceId { get; init; } = default!;
    public DateTimeOffset TimestampUtc { get; init; }

    // Whether the daemon's TunnelAgent currently has a live outbound
    // connection to its configured relay.
    public bool ConnectedToRelay { get; init; }

    // Self-reported TunnelAgentOptions.RelayUrl, so a monitor can match
    // this daemon to the server node hosting its relay.
    public string RelayUrl { get; init; } = default!;

    // Whether a direct or relayed tunnel session is currently piping
    // bytes (one active session per daemon — see ADR-0007).
    public bool SessionActive { get; init; }
}
