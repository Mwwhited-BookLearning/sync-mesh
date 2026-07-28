using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.Ipc;
using SyncMesh.Contracts.OrderBook;
using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Daemon.Demo;

// Alternative order source to SyntheticOrderGenerator — places orders at
// real, live-fetched stock prices instead of random noise. This
// project's first dependency on a live external network service; see
// docs/adr/0008-live-market-data-generator.md for why that's flagged
// explicitly rather than treated as just another generator. Runs
// independently of SyntheticOrderGenerator (both default Enabled=true;
// either can be disabled via config without touching the other).
public sealed class MarketDataOrderGenerator(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<MarketDataOptions> options,
    IOptions<DaemonOptions> daemonOptions,
    ILogger<MarketDataOrderGenerator> logger) : BackgroundService
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

        using var client = httpClientFactory.CreateClient(nameof(MarketDataOrderGenerator));
        client.Timeout = TimeSpan.FromSeconds(10);

        using var timer = new PeriodicTimer(opts.PollInterval);
        do
        {
            try
            {
                await TickAsync(client, opts, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed tick here means, at worst, one fewer real-price
                // order this round — the external API being flaky/rate-
                // limited/unreachable must never crash-loop the daemon or
                // affect anything it actually depends on.
                logger.LogWarning(ex, "Market data generator tick faulted; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(HttpClient client, MarketDataOptions opts, CancellationToken ct)
    {
        if (_ownOpenOrders.Count > 0 && _random.NextDouble() < opts.CancelProbability)
        {
            var index = _random.Next(_ownOpenOrders.Count);
            var orderId = _ownOpenOrders[index];
            _ownOpenOrders.RemoveAt(index);

            using var cancelScope = scopeFactory.CreateScope();
            var cancelWriter = cancelScope.ServiceProvider.GetRequiredService<LocalEventWriter>();
            await cancelWriter.AppendAsync(new AppendEventRequest
            {
                StreamId = orderId,
                EventType = OrderBookEventTypes.OrderCancelled,
                PayloadJson = "{}",
            }, ct);
            return;
        }

        var symbol = opts.Symbols[_random.Next(opts.Symbols.Count)];
        var price = await FetchPriceAsync(client, opts, symbol, ct);
        if (price is null)
        {
            return; // already logged — skip this tick, try again next interval
        }

        var side = _random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        var quantity = Math.Round((decimal)_random.NextDouble() * (opts.MaxQuantity - opts.MinQuantity) + opts.MinQuantity, 0);

        var orderId2 = Guid.NewGuid();
        var payload = new OrderPlaced
        {
            Symbol = symbol,
            Side = side,
            Price = price.Value,
            Quantity = quantity,
            TraderId = $"market-data-{daemonOptions.Value.SiteId}",
        };

        using var scope = scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<LocalEventWriter>();
        await writer.AppendAsync(new AppendEventRequest
        {
            StreamId = orderId2,
            EventType = OrderBookEventTypes.OrderPlaced,
            PayloadJson = JsonSerializer.Serialize(payload),
        }, ct);

        _ownOpenOrders.Add(orderId2);
    }

    private async Task<decimal?> FetchPriceAsync(HttpClient client, MarketDataOptions opts, string symbol, CancellationToken ct)
    {
        var url = $"{opts.BaseUrl}/price?symbol={Uri.EscapeDataString(symbol)}&apikey={Uri.EscapeDataString(opts.ApiKey)}";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to reach the market data API for {Symbol}; skipping this tick.", symbol);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Market data API returned {StatusCode} for {Symbol}; skipping this tick.", response.StatusCode, symbol);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // A plan/key rejection (e.g. the shared "demo" key on any symbol
        // besides AAPL) comes back as 200 OK with {"code":401,"status":
        // "error",...} rather than a non-2xx HTTP status — so "no usable
        // price field" is checked explicitly, not inferred from the
        // status code alone.
        if (!doc.RootElement.TryGetProperty("price", out var priceElement) ||
            priceElement.GetString() is not { } priceText ||
            !decimal.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
        {
            logger.LogWarning("Market data API did not return a usable price for {Symbol}; skipping this tick.", symbol);
            return null;
        }

        return price;
    }
}
