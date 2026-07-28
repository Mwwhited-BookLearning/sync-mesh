namespace SyncMesh.Contracts.OrderBook;

// Example domain built on the generic EventEnvelope/EventRecord machinery
// — a worked demonstration of commands -> events -> a genuine CQRS read
// model (SyncMesh.OrderBook.Api.OrderBookProjector), plus mesh convergence
// (orders placed at different sites folding into one shared book). See
// docs/06-data-model.md's Order Book Example Domain section for the full
// design, especially why StreamId = OrderId (one order = one stream, never
// shared across origins).
public static class OrderBookEventTypes
{
    public const string OrderPlaced = "OrderPlaced";
    public const string OrderCancelled = "OrderCancelled";
}

public enum OrderSide
{
    Buy,
    Sell,
}

// EventEnvelope.PayloadJson shape for OrderBookEventTypes.OrderPlaced.
public sealed class OrderPlaced
{
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal Price { get; init; }
    public required decimal Quantity { get; init; }
    public required string TraderId { get; init; }
}

// EventEnvelope.PayloadJson shape for OrderBookEventTypes.OrderCancelled.
// Empty — the StreamId (== OrderId) already identifies which order.
public sealed class OrderCancelled;
