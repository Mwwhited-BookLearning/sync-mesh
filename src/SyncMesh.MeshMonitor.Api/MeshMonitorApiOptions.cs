using System.ComponentModel.DataAnnotations;

namespace SyncMesh.MeshMonitor.Api;

// Bound from the "MeshMonitor" configuration section — see
// ARCHITECTURE.md → Configuration for the smart-defaults convention this
// project inherits from the rest of the solution.
public sealed class MeshMonitorApiOptions
{
    public const string SectionName = "MeshMonitor";

    // Which NATS endpoints to subscribe to monitor.> on — one per site.
    // Monitor subjects cross leaf/gateway boundaries transparently within
    // a single site's own NATS cluster the same way event-sync subjects
    // do (§4.5), but a multi-site mesh topology (ADR-0002's Phase 3
    // Amendment: point-to-point ServerHost peering, not a shared NATS
    // gateway/supercluster) has no single NATS connection that can see
    // every site's telemetry — each site's hub is its own isolated
    // cluster. A single dashboard covering the whole mesh therefore needs
    // one subscription per site's hub, not one shared connection.
    [Required, MinLength(1)]
    public List<string> NatsUrls { get; set; } = ["nats://localhost:4222"];
}
