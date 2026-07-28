namespace SyncMesh.MeshMonitor.Api.Auth;

// Tickets are short-lived (TicketOptions.Ttl, default 60s) and normally
// self-clean via TryRedeem's unconditional removal — this just catches
// the case a ticket is issued and never redeemed at all (client crashed,
// never connected), so the store doesn't grow unbounded over a long
// uptime. Same periodic-sweep shape as OrderBookProjector/
// MonitorSubscriber's polling loops.
public sealed class TicketCleanupService(ITicketStore store) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.PurgeExpired(DateTimeOffset.UtcNow);
        }
    }
}
