using System.ComponentModel.DataAnnotations;

namespace SyncMesh.MeshMonitor.Api;

// Bound from the "MeshMonitor" configuration section — see
// ARCHITECTURE.md → Configuration for the smart-defaults convention this
// project inherits from the rest of the solution.
public sealed class MeshMonitorApiOptions
{
    public const string SectionName = "MeshMonitor";

    // Which NATS endpoint to subscribe to monitor.> on. Any reachable node
    // works — monitor subjects cross leaf/gateway boundaries transparently
    // the same way event-sync subjects do (§4.5) — this defaults to the
    // hub side, matching where ApplyResponder/ServerMonitorPublisher live.
    [Required]
    public string NatsUrl { get; set; } = "nats://localhost:4222";
}
