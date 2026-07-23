using System.ComponentModel.DataAnnotations;

namespace SyncMesh.Daemon;

// Bound from the "Daemon" configuration section. Every property has a
// smart default so the daemon runs with zero configuration — see
// ARCHITECTURE.md → Configuration.
public sealed class DaemonOptions
{
    public const string SectionName = "Daemon";

    // Identifies this daemon as the originating site for events it
    // records (EventEnvelope.OriginSiteId). Smart default is the machine
    // name; override with a stable, deployment-chosen identifier.
    [Required]
    public string SiteId { get; set; } = Environment.MachineName;

    // Identifies this specific daemon process for passive monitoring
    // (monitor.<siteId>.<instanceId>.*) — distinct from SiteId when
    // multiple daemon instances share one site (e.g. several recording
    // rigs at the same physical location). Smart default assumes one
    // daemon per machine; override when that's not true.
    [Required]
    public string InstanceId { get; set; } = Environment.MachineName;

    // Named pipe the Tier 0 (Local App <-> Local Daemon) IPC listener
    // binds to. See docs/00-design-document.md §4.1.
    [Required]
    public string IpcPipeName { get; set; } = "syncmesh-daemon";
}
