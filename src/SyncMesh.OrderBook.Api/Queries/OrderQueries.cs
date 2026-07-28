namespace SyncMesh.OrderBook.Api.Queries;

// The query side — reads exclusively from IOrderBookStore (the projected
// read model), never touches the write-side EventStoreDbContext directly.
// This separation is the whole point: commands go through the daemon/
// event-sourcing path, queries are served from a denormalized view built
// by replaying those same events (OrderBookProjector).
public static class OrderQueries
{
    public static void MapOrderQueries(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orderbook", (IOrderBookStore store) => Results.Ok(store.Symbols()));

        app.MapGet("/api/orderbook/{symbol}", (string symbol, IOrderBookStore store) =>
            Results.Ok(store.Snapshot(symbol)));
    }
}
