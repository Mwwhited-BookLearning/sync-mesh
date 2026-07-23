using System.ComponentModel.DataAnnotations;

namespace SyncMesh.ServerHost.Nats;

// Bound from the "ServerHost:Nats" configuration section — see
// ARCHITECTURE.md → Configuration for the smart-defaults convention.
public sealed class ServerNatsOptions
{
    public const string SectionName = "ServerHost:Nats";

    // The nearest-server NATS cluster ("hub" side of the leaf connection).
    [Required]
    public string Url { get; set; } = "nats://localhost:4222";

    // Must match DaemonNatsOptions.ApplyRequestSubject on the Daemon side.
    [Required]
    public string ApplyRequestSubject { get; set; } = "server.apply.request";
}
