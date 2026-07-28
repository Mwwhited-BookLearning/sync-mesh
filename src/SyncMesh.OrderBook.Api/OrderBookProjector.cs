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
    private long _lastHlcPhysicalTicks;
    private int _lastHlcLogicalCounter;

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

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        var lastTicks = _lastHlcPhysicalTicks;
        var lastCounter = _lastHlcLogicalCounter;

        var newEvents = await db.Events
            .Where(e => e.EventType == OrderBookEventTypes.OrderPlaced || e.EventType == OrderBookEventTypes.OrderCancelled)
            .Where(e => e.HlcPhysicalTicks > lastTicks ||
                        (e.HlcPhysicalTicks == lastTicks && e.HlcLogicalCounter > lastCounter))
            .OrderBy(e => e.HlcPhysicalTicks).ThenBy(e => e.HlcLogicalCounter)
            .ToListAsync(ct);

        foreach (var record in newEvents)
        {
            Apply(record);
            _lastHlcPhysicalTicks = record.HlcPhysicalTicks;
            _lastHlcLogicalCounter = record.HlcLogicalCounter;
        }
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
