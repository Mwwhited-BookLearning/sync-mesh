using System.Text.Json;
using NATS.Client.Core;
using SyncMesh.Contracts;

// Minimal remote monitoring client (docs/05-implementation-guide.md Phase
// 4): subscribes to monitor.<siteId>.<instanceId>.* and prints each
// DaemonStatus as it arrives. Plain core-NATS subscribe — the same
// interest-graph routing that already carries event-sync traffic across
// leaf/gateway connections carries this too, with no separate
// infrastructure (docs/00-design-document.md §4.5). Connect this to
// whatever NATS endpoint is reachable for the site being monitored (the
// nearest server's hub, or a gateway-connected peer) — it never talks to
// the daemon directly.
if (args.Length < 2)
{
    Console.WriteLine("Usage: SyncMesh.MonitorClient <nats-url> <siteId> [instanceId]");
    Console.WriteLine("  siteId and instanceId accept NATS wildcards, e.g. '*' for \"any\".");
    return 1;
}

var natsUrl = args[0];
var siteId = args[1];
var instanceId = args.Length > 2 ? args[2] : "*";
var subject = $"monitor.{siteId}.{instanceId}.status";

Console.WriteLine($"Connecting to {natsUrl}, subscribing to '{subject}'...");
Console.WriteLine("Press Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });

try
{
    await foreach (var msg in connection.SubscribeAsync<byte[]>(subject, cancellationToken: cts.Token))
    {
        var status = JsonSerializer.Deserialize<DaemonStatus>(msg.Data!);
        if (status is null)
        {
            continue;
        }

        Console.WriteLine(
            $"[{status.TimestampUtc:O}] {status.SiteId}/{status.InstanceId} " +
            $"buffered={status.BufferedEventCount} connectedToNearestServer={status.ConnectedToNearestServer}");
    }
}
catch (OperationCanceledException)
{
}

return 0;
