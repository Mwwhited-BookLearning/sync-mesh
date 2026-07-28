using System.ComponentModel.DataAnnotations;

namespace SyncMesh.OrderBook.Api;

// Bound from the "OrderBook" configuration section — see
// ARCHITECTURE.md -> Configuration for the smart-defaults convention this
// project inherits from the rest of the solution.
public sealed class OrderBookApiOptions
{
    public const string SectionName = "OrderBook";

    // Which daemons' named pipes this API can route PlaceOrder/CancelOrder
    // commands to, keyed by SiteId. Smart default: the two-site demo
    // topology wired up in SyncMesh.AppHost.
    public List<SiteConnection> Sites { get; set; } =
    [
        new SiteConnection { SiteId = "site-a", PipeName = "syncmesh-daemon-a" },
        new SiteConnection { SiteId = "site-b", PipeName = "syncmesh-daemon-b" },
    ];

    // How often OrderBookProjector polls the (read-only) event store for
    // newly applied OrderPlaced/OrderCancelled events.
    public TimeSpan ProjectionPollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class SiteConnection
{
    [Required]
    public string SiteId { get; set; } = default!;

    [Required]
    public string PipeName { get; set; } = default!;
}
