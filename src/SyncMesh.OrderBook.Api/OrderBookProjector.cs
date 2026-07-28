using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.OrderBook;
using SyncMesh.EventStore;

namespace SyncMesh.OrderBook.Api;

// Polls a server-tier EventStoreDbContext (deliberately just ONE of the
// two demo servers' databases — see docs/06-data-model.md's Order Book
// Example Domain section for why: a book built from a single site's
// database showing orders that originated at the OTHER site too is the
// concrete proof the mesh's "every server converges to the same history"
// promise actually holds, not an incidental simplification) and folds
// newly-applied OrderPlaced/OrderCancelled events into IOrderBookStore.
// This IS the CQRS read model this demo exists to prove out.
public sealed class OrderBookProjector(
    IServiceScopeFactory scopeFactory,
    IOrderBookStore store,
    IOptions<OrderBookApiOptions> options,
    ILogger<OrderBookProjector> logger) : BackgroundService
{
    // Not a strict high-water mark on HLC: origin HLC reflects when an
    // event was appended at its ORIGIN site, not when it lands in *this*
    // server's database, so a later-arriving replicated event can have an
    // earlier HLC than one already applied. Querying only ">" a single
    // HLC cursor would then skip that event forever. Instead this tracks
    // the latest RecordedAtUtc seen and re-scans a trailing
    // ProjectionLookbackWindow on every poll; _appliedWithinWindow avoids
    // reapplying the same event redundantly (not required for
    // correctness — Place/Cancel are both idempotent — just avoids
    // wasted work) and is pruned back to the window's contents each poll
    // so it can't grow unboundedly.
    private DateTimeOffset _highWaterMarkUtc = DateTimeOffset.MinValue;
    private readonly HashSet<Guid> _appliedWithinWindow = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ProjectionPollInterval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed poll isn't a correctness problem — the next
                // tick picks up from the same watermark and catches up.
                logger.LogWarning(ex, "Order book projector poll faulted; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        var lookback = options.Value.ProjectionLookbackWindow;
        var since = _highWaterMarkUtc - DateTimeOffset.MinValue > lookback
            ? _highWaterMarkUtc - lookback
            : DateTimeOffset.MinValue;

        // The RecordedAtUtc >= since / OrderBy(RecordedAtUtc) window
        // filtering happens client-side, not in the query itself: EF
        // Core's SQLite provider (this demo's server-tier provider — see
        // Program.cs) cannot translate anything but equality comparisons
        // on DateTimeOffset (a documented provider limitation, not a bug
        // here) and throws rather than producing wrong results. The
        // EventType filter still runs server-side; Order* events are a
        // small enough set for a teaching-scale demo that pulling them
        // into memory for the window filter is a reasonable tradeoff.
        var candidates = (await db.Events
            .Where(e => e.EventType == OrderBookEventTypes.OrderPlaced || e.EventType == OrderBookEventTypes.OrderCancelled)
            .ToListAsync(ct))
            .Where(e => e.RecordedAtUtc >= since)
            .OrderBy(e => e.RecordedAtUtc)
            .ToList();

        foreach (var record in candidates)
        {
            if (_appliedWithinWindow.Add(record.GlobalEventId))
            {
                Apply(record);
            }

            if (record.RecordedAtUtc > _highWaterMarkUtc)
            {
                _highWaterMarkUtc = record.RecordedAtUtc;
            }
        }

        _appliedWithinWindow.IntersectWith(candidates.Select(e => e.GlobalEventId));
    }

    private void Apply(EventRecord record)
    {
        switch (record.EventType)
        {
            case OrderBookEventTypes.OrderPlaced:
                var placed = JsonSerializer.Deserialize<OrderPlaced>(record.PayloadJson)
                    ?? throw new InvalidOperationException($"Empty {OrderBookEventTypes.OrderPlaced} payload for event {record.GlobalEventId}.");
                store.Place(new OrderView(
                    OrderId: record.StreamId,
                    Symbol: placed.Symbol,
                    Side: placed.Side,
                    Price: placed.Price,
                    Quantity: placed.Quantity,
                    TraderId: placed.TraderId,
                    OriginSiteId: record.OriginSiteId,
                    PlacedAtUtc: record.RecordedAtUtc));
                break;

            case OrderBookEventTypes.OrderCancelled:
                store.Cancel(record.StreamId);
                break;
        }
    }
}
