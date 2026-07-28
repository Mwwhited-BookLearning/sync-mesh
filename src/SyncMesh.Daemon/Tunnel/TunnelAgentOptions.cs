using System.ComponentModel.DataAnnotations;

namespace SyncMesh.Daemon.Tunnel;

// Bound from the "Daemon:Tunnel" configuration section. Every property has
// a smart default so the daemon runs with zero configuration — see
// ARCHITECTURE.md → Configuration. TLS + service-credential auth are
// deliberately not part of this options class — see
// docs/adr/0007-custom-reverse-tunnel-mechanism.md and
// PRODUCTION-HARDENING.md for why that's out of scope for this phase.
public sealed class TunnelAgentOptions
{
    public const string SectionName = "Daemon:Tunnel";

    // Local, direct-reachable listener — the "fast path" a remote client
    // tries first, when network topology allows a direct connection.
    [Range(1, 65535)]
    public int DirectListenPort { get; set; } = 7777;

    // Where accepted tunnel connections (direct or relayed) are spliced
    // to — protocol-agnostic raw byte forwarding, matching how frp/chisel
    // themselves work. host:port.
    [Required]
    public string LocalTargetEndpoint { get; set; } = "localhost:9000";

    // The nearest server's tunnel relay — the daemon always dials this
    // outbound, never the reverse, same "daemon dials out" pattern as the
    // NATS leaf node (ADR-0002). host:port.
    [Required]
    public string RelayUrl { get; set; } = "localhost:7778";

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    // TunnelStatus telemetry — see TunnelStatusPublisher. Bundled onto
    // this same options class rather than split out the way
    // DaemonMonitorOptions is split from DaemonNatsOptions: that split
    // exists because monitoring and event-sync are two different
    // subsystems with different failure domains; tunnel telemetry is just
    // a facet of this one subsystem, so it doesn't warrant its own class.
    [Required]
    public string SubjectPrefix { get; set; } = "tunnel";

    public TimeSpan StatusPublishInterval { get; set; } = TimeSpan.FromSeconds(5);
}
