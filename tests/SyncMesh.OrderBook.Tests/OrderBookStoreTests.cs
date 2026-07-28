using SyncMesh.Contracts.OrderBook;
using SyncMesh.OrderBook.Api;

namespace SyncMesh.OrderBook.Tests;

// Unit tests for the actual CQRS read model this demo exists to prove out
// — OrderBookStore's fold logic in isolation, no infrastructure needed.
// See ARCHITECTURE.md's note on why this demo gets unit tests only, not a
// full BDD suite (a worked example, not a phase deliverable).
public sealed class OrderBookStoreTests
{
    private static OrderView MakeOrder(string symbol = "ACME", OrderSide side = OrderSide.Buy, decimal price = 100m, string site = "site-a") =>
        new(Guid.NewGuid(), symbol, side, price, 10m, "trader-1", site, DateTimeOffset.UtcNow);

    [Fact]
    public void Place_SortsBidsDescendingByPrice()
    {
        var store = new OrderBookStore();
        store.Place(MakeOrder(price: 100m));
        store.Place(MakeOrder(price: 105m));
        store.Place(MakeOrder(price: 95m));

        var book = store.Snapshot("ACME");

        Assert.Equal([105m, 100m, 95m], book.Bids.Select(o => o.Price));
    }

    [Fact]
    public void Place_SortsAsksAscendingByPrice()
    {
        var store = new OrderBookStore();
        store.Place(MakeOrder(side: OrderSide.Sell, price: 100m));
        store.Place(MakeOrder(side: OrderSide.Sell, price: 95m));
        store.Place(MakeOrder(side: OrderSide.Sell, price: 105m));

        var book = store.Snapshot("ACME");

        Assert.Equal([95m, 100m, 105m], book.Asks.Select(o => o.Price));
    }

    [Fact]
    public void Cancel_RemovesTheOrder()
    {
        var store = new OrderBookStore();
        var order = MakeOrder();
        store.Place(order);

        store.Cancel(order.OrderId);

        var book = store.Snapshot(order.Symbol);
        Assert.Empty(book.Bids);
        Assert.Empty(book.Asks);
    }

    [Fact]
    public void Cancel_UnknownOrderId_IsANoOp()
    {
        var store = new OrderBookStore();
        store.Place(MakeOrder());

        store.Cancel(Guid.NewGuid()); // never placed — must not throw

        Assert.Single(store.Snapshot("ACME").Bids);
    }

    [Fact]
    public void Snapshot_UnknownSymbol_ReturnsEmptyBookNotNull()
    {
        var store = new OrderBookStore();

        var book = store.Snapshot("NEVER-TRADED");

        Assert.Empty(book.Bids);
        Assert.Empty(book.Asks);
    }

    [Fact]
    public void Symbols_TracksOnlySymbolsWithCurrentlyOpenOrders()
    {
        var store = new OrderBookStore();
        var acme = MakeOrder(symbol: "ACME");
        var globex = MakeOrder(symbol: "GLOBEX");
        store.Place(acme);
        store.Place(globex);

        Assert.Equal(["ACME", "GLOBEX"], store.Symbols().OrderBy(s => s));

        store.Cancel(acme.OrderId);

        Assert.Equal(["GLOBEX"], store.Symbols());
    }

    [Fact]
    public void OrdersFromDifferentSites_ConvergeIntoOneBook()
    {
        // The whole point of this read model: orders that originated at
        // different sites (different OriginSiteId) end up in the same
        // book, indistinguishable except for that field.
        var store = new OrderBookStore();
        store.Place(MakeOrder(site: "site-a", price: 100m));
        store.Place(MakeOrder(side: OrderSide.Sell, site: "site-b", price: 101m));

        var book = store.Snapshot("ACME");

        Assert.Equal("site-a", Assert.Single(book.Bids).OriginSiteId);
        Assert.Equal("site-b", Assert.Single(book.Asks).OriginSiteId);
    }
}
