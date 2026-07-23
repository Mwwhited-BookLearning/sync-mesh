using System.ComponentModel.DataAnnotations;

namespace SyncMesh.Daemon.Nats;

// Bound from the "Daemon:Monitor" configuration section — see
// ARCHITECTURE.md → Configuration. Deliberately its own options class and
// subject namespace, separate from Daemon:Nats (event sync) — see
// docs/00-design-document.md §4.5 and CLAUDE.md working agreement #6:
// monitoring and event-sync stay architecturally separate so a failure or
// config change in one never touches the other.
public sealed class DaemonMonitorOptions
{
    public const string SectionName = "Daemon:Monitor";

    [Required]
    public string SubjectPrefix { get; set; } = "monitor";

    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(5);
}
