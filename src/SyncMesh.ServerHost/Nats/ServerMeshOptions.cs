using System.ComponentModel.DataAnnotations;

namespace SyncMesh.ServerHost.Nats;

// Bound from the "ServerHost:Mesh" configuration section — see
// ARCHITECTURE.md → Configuration for the smart-defaults convention.
// See docs/adr/0002-nats-leaf-nodes-for-transport.md's 2026-07-23 (Phase 3)
// Amendment for the replication mechanism this configures.
public sealed class ServerMeshOptions
{
    public const string SectionName = "ServerHost:Mesh";

    [Required]
    public string OutboundStreamName { get; set; } = "MESH_OUTBOUND";

    [Required]
    public string OutboundSubjectPrefix { get; set; } = "mesh.outbound";

    public List<MeshPeerOptions> Peers { get; set; } = [];

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // How long JetStream waits for an ack before redelivering an
    // unacknowledged message to a peer's consumer. Shorter than JetStream's
    // own 30s default: a failed forward here is most often a transient
    // startup race or a brief peer blip, not a real multi-minute outage —
    // see ARCHITECTURE.md for the specific race this was tuned against.
    public TimeSpan AckWait { get; set; } = TimeSpan.FromSeconds(5);

    // Ceiling for the outbound relay buffer — same disk-bound-by-default
    // floor/ceiling philosophy as the daemon's own buffer (§4.2): never
    // discard an event before every configured peer has acked it; default
    // to unbounded except by available local disk; reject new relay
    // publishes (Discard: New in ServerMeshSetup) rather than evict
    // unacknowledged data once genuinely full.
    public long MaxBytes { get; set; } = -1;
    public long MaxMsgs { get; set; } = -1;
    public TimeSpan MaxAge { get; set; } = TimeSpan.Zero;
}

public sealed class MeshPeerOptions
{
    // Used to name this peer's durable consumer on MESH_OUTBOUND
    // (`TO_<SiteId>`) — must be unique among this server's configured peers.
    [Required]
    public string SiteId { get; set; } = default!;

    // NATS URL for the peer's own cluster — a direct point-to-point
    // connection, not routed through any shared gateway/leaf topology.
    [Required]
    public string Url { get; set; } = default!;

    // Must match the peer's own ServerNatsOptions.ApplyRequestSubject.
    [Required]
    public string ApplyRequestSubject { get; set; } = "server.apply.request";
}
