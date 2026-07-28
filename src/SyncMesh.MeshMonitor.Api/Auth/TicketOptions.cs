using System.ComponentModel.DataAnnotations;

namespace SyncMesh.MeshMonitor.Api.Auth;

// Bound from the "MeshMonitor:Ticket" configuration section.
public sealed class TicketOptions
{
    public const string SectionName = "MeshMonitor:Ticket";

    // How long a ticket is redeemable for after POST /auth/ticket issues
    // it. Short by design — a client is expected to redeem it within
    // moments of receiving it (e.g. immediately opening the SignalR
    // connection it was requested for), not carry it around.
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);
}
