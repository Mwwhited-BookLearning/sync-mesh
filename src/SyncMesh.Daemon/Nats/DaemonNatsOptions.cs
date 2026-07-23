using System.ComponentModel.DataAnnotations;

namespace SyncMesh.Daemon.Nats;

// Bound from the "Daemon:Nats" configuration section — see ARCHITECTURE.md
// → Configuration for the smart-defaults convention.
public sealed class DaemonNatsOptions
{
    public const string SectionName = "Daemon:Nats";

    // The daemon's local embedded NATS leaf node — never the nearest
    // server directly. See docs/00-design-document.md §4.2.
    [Required]
    public string Url { get; set; } = "nats://localhost:4222";

    [Required]
    public string StreamName { get; set; } = "DAEMON_EVENTS";

    [Required]
    public string SubjectPrefix { get; set; } = "events";

    [Required]
    public string ConsumerName { get; set; } = "FORWARDER";

    // Must match ServerNatsOptions.ApplyRequestSubject on the ServerHost
    // side — this is the core-NATS request subject the forwarder sends to
    // and the hub-side responder listens on. Core NATS request/reply
    // crosses the leaf-node boundary transparently; no special config
    // needed beyond the leaf connection itself.
    [Required]
    public string ApplyRequestSubject { get; set; } = "server.apply.request";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
