using System.Text.Json;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.Ipc;
using SyncMesh.Contracts.OrderBook;

namespace SyncMesh.OrderBook.Api.Commands;

public sealed record PlaceOrderRequest(string SiteId, string Symbol, OrderSide Side, decimal Price, decimal Quantity, string TraderId);
public sealed record PlaceOrderResponse(Guid OrderId);
public sealed record CancelOrderRequest(string SiteId);

// The command side: this API plays the role of "the local app" (see
// SyncMesh.Contracts.Ipc.LocalIpcClient's own doc comment — "stands in for
// the local app until a real one exists"), routing each command through
// the named daemon's IPC pipe for the requested SiteId. This exercises
// the real Local App -> Daemon -> Server -> Mesh path, not a shortcut.
//
// StreamId = OrderId for every command here — see
// docs/06-data-model.md's Order Book Example Domain section for why one
// order must be exactly one stream, owned by whichever daemon placed it.
public static class OrderCommands
{
    public static void MapOrderCommands(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orders", async (PlaceOrderRequest request, IOptions<OrderBookApiOptions> options, CancellationToken ct) =>
        {
            var site = options.Value.Sites.Find(s => s.SiteId == request.SiteId);
            if (site is null)
            {
                return Results.BadRequest($"Unknown site '{request.SiteId}'.");
            }

            var orderId = Guid.NewGuid();
            var payload = new OrderPlaced
            {
                Symbol = request.Symbol,
                Side = request.Side,
                Price = request.Price,
                Quantity = request.Quantity,
                TraderId = request.TraderId,
            };

            var client = new LocalIpcClient(site.PipeName);
            await client.AppendEventAsync(new AppendEventRequest
            {
                StreamId = orderId,
                EventType = OrderBookEventTypes.OrderPlaced,
                PayloadJson = JsonSerializer.Serialize(payload),
            }, ct);

            return Results.Ok(new PlaceOrderResponse(orderId));
        });

        // Must be routed through the SAME site that placed the order —
        // that daemon is the only one that owns this stream's version
        // sequence. The test UI supplies SiteId from the order's own
        // OriginSiteId (visible in the order book view) so this is
        // automatic, not something a user has to track manually.
        app.MapPost("/api/orders/{orderId:guid}/cancel", async (Guid orderId, CancelOrderRequest request, IOptions<OrderBookApiOptions> options, CancellationToken ct) =>
        {
            var site = options.Value.Sites.Find(s => s.SiteId == request.SiteId);
            if (site is null)
            {
                return Results.BadRequest($"Unknown site '{request.SiteId}'.");
            }

            var client = new LocalIpcClient(site.PipeName);
            await client.AppendEventAsync(new AppendEventRequest
            {
                StreamId = orderId,
                EventType = OrderBookEventTypes.OrderCancelled,
                PayloadJson = "{}",
            }, ct);

            return Results.NoContent();
        });
    }
}
