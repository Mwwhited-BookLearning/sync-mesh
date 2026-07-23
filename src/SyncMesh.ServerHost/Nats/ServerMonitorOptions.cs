using System.ComponentModel.DataAnnotations;

namespace SyncMesh.ServerHost.Nats;

// Bound from the "ServerHost:Monitor" configuration section — see
// ARCHITECTURE.md → Configuration. Mirrors SyncMesh.Daemon.Nats
// .DaemonMonitorOptions; kept as its own options class/subject namespace
// for the same reason (CLAUDE.md working agreement #6): monitoring and
// event-sync stay architecturally separate.
public sealed class ServerMonitorOptions
{
    public const string SectionName = "ServerHost:Monitor";

    // This server's own identity for monitoring purposes — distinct from
    // any daemon's OriginSiteId; a server never originates events itself.
    // Smart default assumes one server process per machine.
    [Required]
    public string SiteId { get; set; } = Environment.MachineName;

    [Required]
    public string InstanceId { get; set; } = Environment.MachineName;

    [Required]
    public string SubjectPrefix { get; set; } = "monitor";

    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(5);
}
