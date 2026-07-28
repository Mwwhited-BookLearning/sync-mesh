using System.Text.Json;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.Ipc;
using SyncMesh.Contracts.OrderBook;
using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Daemon.Demo;

// Generates continuous order-book traffic from this daemon (leaf node) so
// mesh replication is something you can actually watch happen, not just
// something proven in a test. Writes in-process via LocalEventWriter —
// no IPC round-trip needed, this daemon already owns that store — using
// the exact same example domain (SyncMesh.Contracts.OrderBook) the
// SyncMesh.OrderBook.Api demo commands/queries use, so synthetic and
// human-placed orders are indistinguishable to the read model.
public sealed class SyntheticOrderGenerator(
    IServiceScopeFactory scopeFactory,
    IOptions<SyntheticOrderGeneratorOptions> options,
    IOptions<DaemonOptions> daemonOptions,
    ILogger<SyntheticOrderGenerator> logger) : BackgroundService
{
    private readonly List<Guid> _ownOpenOrders = [];
    private readonly Random _random = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(opts.Interval);
        do
        {
            try
            {
                await TickAsync(opts, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed tick just means one fewer synthetic order this
                // round — nothing here depends on strict cadence.
                logger.LogWarning(ex, "Synthetic order generator tick faulted; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(SyntheticOrderGeneratorOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<LocalEventWriter>();

        if (_ownOpenOrders.Count > 0 && _random.NextDouble() < opts.CancelProbability)
        {
            var index = _random.Next(_ownOpenOrders.Count);
            var orderId = _ownOpenOrders[index];
            _ownOpenOrders.RemoveAt(index);

            await writer.AppendAsync(new AppendEventRequest
            {
                StreamId = orderId,
                EventType = OrderBookEventTypes.OrderCancelled,
                PayloadJson = "{}",
            }, ct);
            return;
        }

        var symbol = opts.Symbols[_random.Next(opts.Symbols.Count)];
        var side = _random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        var price = Math.Round((decimal)_random.NextDouble() * (opts.MaxPrice - opts.MinPrice) + opts.MinPrice, 2);
        var quantity = Math.Round((decimal)_random.NextDouble() * (opts.MaxQuantity - opts.MinQuantity) + opts.MinQuantity, 0);

        var orderId2 = Guid.NewGuid();
        var payload = new OrderPlaced
        {
            Symbol = symbol,
            Side = side,
            Price = price,
            Quantity = quantity,
            TraderId = $"synthetic-{daemonOptions.Value.SiteId}",
        };

        await writer.AppendAsync(new AppendEventRequest
        {
            StreamId = orderId2,
            EventType = OrderBookEventTypes.OrderPlaced,
            PayloadJson = JsonSerializer.Serialize(payload),
        }, ct);

        _ownOpenOrders.Add(orderId2);
    }
}
