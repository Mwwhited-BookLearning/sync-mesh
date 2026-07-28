using System.Net;
using System.Net.Sockets;
using SyncMesh.Contracts.Tunnel;
using SyncMesh.TunnelClient;

// Minimal remote tunnel client (docs/adr/0007-custom-reverse-tunnel-
// mechanism.md): listens locally and, per accepted connection, tries a
// direct connection to the daemon first, falling back to relaying
// through the nearest server's TunnelRelay when direct is blocked. See
// TunnelConnector for the actual direct-first/relay-fallback logic.
var options = CliOptions.Parse(args);
if (options is null)
{
    Console.WriteLine("Usage: SyncMesh.TunnelClient --direct host:port --relay host:port --site X --instance Y --listen localPort [--timeout seconds]");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var listener = new TcpListener(IPAddress.Loopback, options.LocalListenPort);
listener.Start();
Console.WriteLine($"Listening on localhost:{options.LocalListenPort} — forwarding to {options.SiteId}/{options.InstanceId} (direct {options.DirectHost}:{options.DirectPort}, relay {options.RelayHost}:{options.RelayPort}).");
Console.WriteLine("Press Ctrl+C to stop.");

try
{
    while (!cts.IsCancellationRequested)
    {
        var local = await listener.AcceptTcpClientAsync(cts.Token);
        _ = HandleConnectionAsync(local, options, cts.Token);
    }
}
catch (OperationCanceledException)
{
}

return 0;

static async Task HandleConnectionAsync(TcpClient local, CliOptions options, CancellationToken ct)
{
    using (local)
    {
        try
        {
            var result = await TunnelConnector.ConnectAsync(
                options.DirectHost, options.DirectPort,
                options.RelayHost, options.RelayPort,
                options.SiteId, options.InstanceId,
                options.DirectAttemptTimeout, ct);

            Console.WriteLine($"Connected via {(result.UsedRelay ? "relay" : "direct")} path.");
            await TunnelFraming.SpliceAsync(local.GetStream(), result.Stream, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Tunnel session ended with an error: {ex.Message}");
        }
    }
}

internal sealed class CliOptions
{
    public required string DirectHost { get; init; }
    public required int DirectPort { get; init; }
    public required string RelayHost { get; init; }
    public required int RelayPort { get; init; }
    public required string SiteId { get; init; }
    public required string InstanceId { get; init; }
    public required int LocalListenPort { get; init; }
    public TimeSpan DirectAttemptTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public static CliOptions? Parse(string[] args)
    {
        string? direct = null, relay = null, site = null, instance = null, listen = null, timeout = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--direct": direct = args[++i]; break;
                case "--relay": relay = args[++i]; break;
                case "--site": site = args[++i]; break;
                case "--instance": instance = args[++i]; break;
                case "--listen": listen = args[++i]; break;
                case "--timeout": timeout = args[++i]; break;
            }
        }

        if (direct is null || relay is null || site is null || instance is null || listen is null)
        {
            return null;
        }

        var (directHost, directPort) = ParseEndpoint(direct);
        var (relayHost, relayPort) = ParseEndpoint(relay);

        return new CliOptions
        {
            DirectHost = directHost,
            DirectPort = directPort,
            RelayHost = relayHost,
            RelayPort = relayPort,
            SiteId = site,
            InstanceId = instance,
            LocalListenPort = int.Parse(listen),
            DirectAttemptTimeout = timeout is null ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(double.Parse(timeout)),
        };
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var separatorIndex = endpoint.LastIndexOf(':');
        return (endpoint[..separatorIndex], int.Parse(endpoint[(separatorIndex + 1)..]));
    }
}
