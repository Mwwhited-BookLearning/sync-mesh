using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Contracts.Ipc;
using SyncMesh.Daemon.Ipc;
using SyncMesh.Daemon.Nats;
using SyncMesh.Daemon.Tunnel;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;
using SyncMesh.ServerHost.Tunnel;
using SyncMesh.TunnelClient;

namespace SyncMesh.Sync.Tests;

// Proves ADR-0004's core requirement — event-sync and the interactive
// tunnel are architecturally independent failure domains — against a
// real combined stack: real nats-server hub+leaf containers for
// event-sync, and the real plain-TCP TunnelAgent/TunnelRelay for the
// tunnel (see docs/adr/0007-custom-reverse-tunnel-mechanism.md). Not a
// mock: both directions of failure are proven by actually killing one
// mechanism and exercising the other against real infrastructure.
public sealed class TunnelFailureIsolationTests(NatsLeafHubFixture fixture) : IClassFixture<NatsLeafHubFixture>, IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<string> _dbPaths = [];

    [Fact]
    public async Task TunnelKilled_EventSyncUnaffected()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var (relayAgentPort, relayClientPort) = (NatsLeafHubFixture.GetFreeTcpPort(), NatsLeafHubFixture.GetFreeTcpPort());
        var (daemon, _) = await BuildDaemonSideAsync(testId, relayAgentPort);
        var (server, serverDb) = await BuildServerSideAsync(testId, relayAgentPort, relayClientPort);
        await using var echo = EchoServer.Start();
        daemon.TunnelAgentOptions.LocalTargetEndpoint = $"localhost:{echo.Port}";

        await daemon.JetStreamSetup.StartAsync(CancellationToken.None);
        using var forwarderCts = new CancellationTokenSource();
        _ = daemon.Forwarder.StartAsync(forwarderCts.Token);
        using var responderCts = new CancellationTokenSource();
        _ = server.Responder.StartAsync(responderCts.Token);
        using var tunnelAgentCts = new CancellationTokenSource();
        _ = daemon.TunnelAgent.StartAsync(tunnelAgentCts.Token);
        using var tunnelRelayCts = new CancellationTokenSource();
        _ = server.TunnelRelay.StartAsync(tunnelRelayCts.Token);

        await WaitUntilAsync(() => daemon.TunnelAgent.ConnectedToRelay);

        // Sanity check: the tunnel actually works before killing it.
        await AssertTunnelRoundTripsAsync(daemon, server);

        // Kill the tunnel path — the relay stops accepting new client/agent
        // connections (existing ones fault too, since their accept loops
        // are what own the sockets).
        await tunnelRelayCts.CancelAsync();

        // Event-sync must be completely unaffected.
        var streamId = Guid.NewGuid();
        var response = await daemon.Writer.AppendAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "AfterTunnelKilled",
            PayloadJson = "{}",
        }, CancellationToken.None);
        var applied = await WaitUntilAppliedAsync(serverDb, response.GlobalEventId);
        Assert.NotNull(applied);
        Assert.True(daemon.Forwarder.ForwardedCount > 0);

        await forwarderCts.CancelAsync();
        await responderCts.CancelAsync();
        await tunnelAgentCts.CancelAsync();
    }

    [Fact]
    public async Task EventSyncKilled_TunnelUnaffected()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var (relayAgentPort, relayClientPort) = (NatsLeafHubFixture.GetFreeTcpPort(), NatsLeafHubFixture.GetFreeTcpPort());
        var (daemon, _) = await BuildDaemonSideAsync(testId, relayAgentPort);
        var (server, serverDb) = await BuildServerSideAsync(testId, relayAgentPort, relayClientPort);
        await using var echo = EchoServer.Start();
        daemon.TunnelAgentOptions.LocalTargetEndpoint = $"localhost:{echo.Port}";

        await daemon.JetStreamSetup.StartAsync(CancellationToken.None);
        using var forwarderCts = new CancellationTokenSource();
        _ = daemon.Forwarder.StartAsync(forwarderCts.Token);
        using var responderCts = new CancellationTokenSource();
        _ = server.Responder.StartAsync(responderCts.Token);
        using var tunnelAgentCts = new CancellationTokenSource();
        _ = daemon.TunnelAgent.StartAsync(tunnelAgentCts.Token);
        using var tunnelRelayCts = new CancellationTokenSource();
        _ = server.TunnelRelay.StartAsync(tunnelRelayCts.Token);

        await WaitUntilAsync(() => daemon.TunnelAgent.ConnectedToRelay);

        // Sanity check: event-sync works before the outage.
        var streamId = Guid.NewGuid();
        var beforeOutage = await daemon.Writer.AppendAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "BeforeOutage",
            PayloadJson = "{}",
        }, CancellationToken.None);
        Assert.NotNull(await WaitUntilAppliedAsync(serverDb, beforeOutage.GlobalEventId));

        try
        {
            await fixture.StopHubAsync();

            // Sanity check: a new event genuinely does NOT reach the
            // server while the hub is down.
            var duringOutage = await daemon.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = streamId,
                EventType = "DuringOutage",
                PayloadJson = "{}",
            }, CancellationToken.None);
            Assert.Null(await serverDb.Events.FirstOrDefaultAsync(e => e.GlobalEventId == duringOutage.GlobalEventId));

            // The tunnel must be completely unaffected — it has zero NATS
            // dependency by construction (no reference to NatsConnection/
            // NatsJSContext anywhere in Tunnel/).
            await AssertTunnelRoundTripsAsync(daemon, server);
        }
        finally
        {
            await fixture.StartHubAsync();
        }

        await forwarderCts.CancelAsync();
        await responderCts.CancelAsync();
        await tunnelAgentCts.CancelAsync();
        await tunnelRelayCts.CancelAsync();
    }

    private static async Task AssertTunnelRoundTripsAsync(DaemonHarness daemon, ServerHarness server)
    {
        // Force the relay path (an unreachable direct target) — these
        // tests care that the tunnel mechanism itself works and is
        // failure-isolated from event-sync, not the direct-vs-relay
        // choice, which is covered by the BDD scenarios instead.
        var result = await TunnelConnector.ConnectAsync(
            directHost: "localhost", directPort: 1,
            relayHost: "localhost", relayPort: server.TunnelRelayOptions.ClientListenPort,
            siteId: daemon.SiteId, instanceId: daemon.InstanceId,
            directAttemptTimeout: TimeSpan.FromMilliseconds(300),
            ct: CancellationToken.None);

        Assert.True(result.UsedRelay);

        var payload = Guid.NewGuid().ToByteArray();
        await result.Stream.WriteAsync(payload);
        var echoed = new byte[payload.Length];
        var offset = 0;
        while (offset < echoed.Length)
        {
            var read = await result.Stream.ReadAsync(echoed.AsMemory(offset));
            Assert.True(read > 0);
            offset += read;
        }
        Assert.Equal(payload, echoed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.True(condition(), "Condition was not met within the timeout.");
    }

    private static async Task<EventRecord?> WaitUntilAppliedAsync(EventStoreDbContext db, Guid globalEventId, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        EventRecord? applied = null;
        while (applied is null && DateTimeOffset.UtcNow < deadline)
        {
            applied = await db.Events.FirstOrDefaultAsync(e => e.GlobalEventId == globalEventId);
            if (applied is null)
            {
                await Task.Delay(200);
            }
        }
        return applied;
    }

    private async Task<(DaemonHarness Harness, EventStoreDbContext Db)> BuildDaemonSideAsync(string testId, int relayAgentPort)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-tunnel-daemon-{testId}.db");
        _dbPaths.Add(dbPath);

        var siteId = $"daemon-{testId}";
        var instanceId = $"instance-{testId}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={dbPath}");
        services.AddSingleton<HlcGenerator>();
        services.Configure<DaemonOptions>(o => { o.SiteId = siteId; o.InstanceId = instanceId; });
        services.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = fixture.LeafClientUrl;
            o.StreamName = $"DAEMON_EVENTS_{testId}";
            o.ConsumerName = $"FORWARDER_{testId}";
            o.SubjectPrefix = $"events{testId}";
            o.ApplyRequestSubject = $"server.apply.request.{testId}";
        });
        services.Configure<TunnelAgentOptions>(o =>
        {
            o.DirectListenPort = NatsLeafHubFixture.GetFreeTcpPort();
            o.RelayUrl = $"localhost:{relayAgentPort}";
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddScoped<LocalEventWriter>();
        services.AddSingleton<DaemonJetStreamSetup>();
        services.AddSingleton<EventForwarder>();
        services.AddSingleton<TunnelAgent>();

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        var harness = new DaemonHarness(
            Writer: provider.GetRequiredService<LocalEventWriter>(),
            JetStreamSetup: provider.GetRequiredService<DaemonJetStreamSetup>(),
            Forwarder: provider.GetRequiredService<EventForwarder>(),
            TunnelAgent: provider.GetRequiredService<TunnelAgent>(),
            TunnelAgentOptions: provider.GetRequiredService<IOptions<TunnelAgentOptions>>().Value,
            SiteId: siteId,
            InstanceId: instanceId);

        return (harness, provider.GetRequiredService<EventStoreDbContext>());
    }

    private async Task<(ServerHarness Harness, EventStoreDbContext Db)> BuildServerSideAsync(string testId, int agentPort, int clientPort)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-tunnel-server-{testId}.db");
        _dbPaths.Add(dbPath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={dbPath}");
        services.Configure<ServerNatsOptions>(o =>
        {
            o.Url = fixture.HubClientUrl;
            o.ApplyRequestSubject = $"server.apply.request.{testId}";
        });
        services.Configure<TunnelRelayOptions>(o =>
        {
            o.AgentListenPort = agentPort;
            o.ClientListenPort = clientPort;
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<ServerNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddSingleton<ApplyResponder>();
        services.AddSingleton<TunnelRelay>();

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        var harness = new ServerHarness(
            Responder: provider.GetRequiredService<ApplyResponder>(),
            TunnelRelay: provider.GetRequiredService<TunnelRelay>(),
            TunnelRelayOptions: provider.GetRequiredService<IOptions<TunnelRelayOptions>>().Value);

        return (harness, provider.GetRequiredService<EventStoreDbContext>());
    }

    private sealed record DaemonHarness(
        LocalEventWriter Writer,
        DaemonJetStreamSetup JetStreamSetup,
        EventForwarder Forwarder,
        TunnelAgent TunnelAgent,
        TunnelAgentOptions TunnelAgentOptions,
        string SiteId,
        string InstanceId);

    private sealed record ServerHarness(ApplyResponder Responder, TunnelRelay TunnelRelay, TunnelRelayOptions TunnelRelayOptions);

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        foreach (var path in _dbPaths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
