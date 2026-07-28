using System.ComponentModel.DataAnnotations;

namespace SyncMesh.ServerHost.Tunnel;

// Bound from the "ServerHost:Tunnel" configuration section. Every property
// has a smart default so the server runs with zero configuration — see
// ARCHITECTURE.md → Configuration. TLS + service-credential auth are
// deliberately not part of this options class — see
// docs/adr/0007-custom-reverse-tunnel-mechanism.md and
// PRODUCTION-HARDENING.md for why that's out of scope for this phase.
public sealed class TunnelRelayOptions
{
    public const string SectionName = "ServerHost:Tunnel";

    // Daemons' TunnelAgent control/data connections dial in here.
    [Range(1, 65535)]
    public int AgentListenPort { get; set; } = 7778;

    // Remote tunnel clients (SyncMesh.TunnelClient's relay-fallback path)
    // dial in here.
    [Range(1, 65535)]
    public int ClientListenPort { get; set; } = 7779;

    // How long a client connection waits for the requested agent to open
    // its data channel before giving up.
    public TimeSpan SessionWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    // An agent's control connection is dropped from the registry if no
    // Heartbeat/traffic is observed within this window.
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
