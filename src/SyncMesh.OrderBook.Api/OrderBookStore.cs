using System.Collections.Concurrent;
using SyncMesh.Contracts.OrderBook;

namespace SyncMesh.OrderBook.Api;

// One open order, folded from an OrderPlaced event — see OrderBookProjector.
public sealed record OrderView(
    Guid OrderId,
    string Symbol,
    OrderSide Side,
    decimal Price,
    decimal Quantity,
    string TraderId,
    string OriginSiteId,
    DateTimeOffset PlacedAtUtc);

public sealed record OrderBookView(IReadOnlyList<OrderView> Bids, IReadOnlyList<OrderView> Asks);

public interface IOrderBookStore
{
    void Place(OrderView order);
    void Cancel(Guid orderId);
    OrderBookView Snapshot(string symbol);
    IReadOnlyCollection<string> Symbols();
}

// The actual CQRS read model this demo exists to prove out — a
// denormalized, queryable view folded from replaying OrderPlaced/
// OrderCancelled events (see OrderBookProjector), genuinely distinct from
// the write-side EventRecord table, unlike SyncMesh.Daemon.Ipc
// .LocalEventReader (which just re-queries the same table it wrote to).
//
// Keyed flat by OrderId (not nested by symbol) deliberately: an
// OrderCancelled event only carries the orderId (== StreamId), not the
// symbol, so cancellation must be resolvable without already knowing
// which symbol's book to look in.
public sealed class OrderBookStore : IOrderBookStore
{
    private readonly ConcurrentDictionary<Guid, OrderView> _orders = new();

    // Tombstones every cancelled order id, permanently. Without this, a
    // Cancel that arrives before its Place (CLAUDE.md rule 4: consumers
    // must tolerate out-of-local-arrival-order delivery — a genuinely
    // possible replay/replication ordering, not a hypothetical) would
    // no-op against an empty _orders, and the later Place would then
    // resurrect the cancelled order permanently. Unbounded growth is an
    // accepted POC-scale tradeoff, same as this store's own in-memory
    // _orders dictionary having no eviction.
    private readonly ConcurrentDictionary<Guid, byte> _cancelledOrderIds = new();

    public void Place(OrderView order)
    {
        if (_cancelledOrderIds.ContainsKey(order.OrderId))
        {
            return;
        }

        _orders.TryAdd(order.OrderId, order);
    }

    public void Cancel(Guid orderId)
    {
        _cancelledOrderIds[orderId] = 0;
        _orders.TryRemove(orderId, out _);
    }

    public OrderBookView Snapshot(string symbol)
    {
        var matching = _orders.Values.Where(o => o.Symbol == symbol).ToList();
        return new OrderBookView(
            Bids: matching.Where(o => o.Side == OrderSide.Buy).OrderByDescending(o => o.Price).ToList(),
            Asks: matching.Where(o => o.Side == OrderSide.Sell).OrderBy(o => o.Price).ToList());
    }

    public IReadOnlyCollection<string> Symbols() =>
        _orders.Values.Select(o => o.Symbol).Distinct().ToList();
}
