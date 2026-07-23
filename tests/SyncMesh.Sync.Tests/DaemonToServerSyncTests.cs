using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Ipc;
using SyncMesh.Daemon.Nats;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;
using Xunit.Abstractions;

namespace SyncMesh.Sync.Tests;

// Proves the Phase 2 exit criteria end to end against real nats-server
// containers (hub + leaf, real leaf-node config) — not a mock. See
// docs/05-implementation-guide.md Phase 2 and ADR-0002's 2026-07-23
// Amendment.
public sealed class DaemonToServerSyncTests(NatsLeafHubFixture fixture, ITestOutputHelper output) : IClassFixture<NatsLeafHubFixture>, IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<string> _dbPaths = [];

    [Fact]
    public async Task Event_writtenAtTheDaemon_reachesTheServerAndIsRemovedFromTheLocalBuffer()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var (daemon, _) = await BuildDaemonSideAsync(testId);
        var (server, serverDb) = await BuildServerSideAsync(testId);

        await daemon.JetStreamSetup.StartAsync(CancellationToken.None);
        using var forwarderCts = new CancellationTokenSource();
        _ = daemon.Forwarder.StartAsync(forwarderCts.Token);
        using var responderCts = new CancellationTokenSource();
        _ = server.Responder.StartAsync(responderCts.Token);

        var streamId = Guid.NewGuid();
        var appendResponse = await daemon.Writer.AppendAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "TestEvent",
            PayloadJson = """{"value":1}""",
        }, CancellationToken.None);

        // Poll: the forwarder/responder round trip is async.
        EventRecord? applied = null;
        for (var i = 0; i < 100 && applied is null; i++)
        {
            applied = await serverDb.Events.FirstOrDefaultAsync(e => e.GlobalEventId == appendResponse.GlobalEventId);
            if (applied is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(applied);
        Assert.Equal(streamId, applied.StreamId);

        // And the local WorkQueue buffer should have drained (acked).
        var streamInfo = await daemon.JetStream.GetStreamAsync(daemon.NatsOptions.StreamName);
        int i2 = 0;
        while (streamInfo.Info.State.Messages != 0 && i2++ < 50)
        {
            await Task.Delay(100);
            streamInfo = await daemon.JetStream.GetStreamAsync(daemon.NatsOptions.StreamName);
        }
        Assert.Equal(0u, streamInfo.Info.State.Messages);

        await forwarderCts.CancelAsync();
        await responderCts.CancelAsync();
    }

    // Phase 2 exit criteria, explicitly: "simulate an extended
    // disconnect/reconnect and verify event delivery" — this directly
    // tests the leaf-node reconnect-sync risk flagged in ADR-0002 / design
    // doc Open Question 2. Not a smoke test: the hub container is actually
    // stopped (not just network-partitioned), forcing the leaf's leaf-node
    // connection to drop and re-establish from scratch, exactly like the
    // manual validation in ADR-0002's 2026-07-23 Amendment.
    [Fact]
    public async Task ExtendedDisconnectThenReconnect_AllBufferedEventsEventuallyReachTheServer_NoLossNoDuplication()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var (daemon, _) = await BuildDaemonSideAsync(testId);
        var (server, serverDb) = await BuildServerSideAsync(testId);

        await daemon.JetStreamSetup.StartAsync(CancellationToken.None);
        using var forwarderCts = new CancellationTokenSource();
        _ = daemon.Forwarder.StartAsync(forwarderCts.Token);
        using var responderCts = new CancellationTokenSource();
        _ = server.Responder.StartAsync(responderCts.Token);

        // Confirm the path works before the outage.
        var streamId = Guid.NewGuid();
        var beforeOutage = await daemon.Writer.AppendAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "BeforeOutage",
            PayloadJson = "{}",
        }, CancellationToken.None);
        await WaitUntilAppliedAsync(serverDb, beforeOutage.GlobalEventId);

        // Extended outage: stop the hub entirely (not just a network
        // blip) — longer than the leaf's normal reconnect backoff.
        await fixture.StopHubAsync();

        var duringOutage = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var response = await daemon.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = streamId,
                EventType = "DuringOutage",
                PayloadJson = $$"""{"seq":{{i}}}""",
            }, CancellationToken.None);
            duringOutage.Add(response.GlobalEventId);
        }

        // These must NOT have reached the server while the hub is down.
        foreach (var globalEventId in duringOutage)
        {
            var applied = await serverDb.Events.FirstOrDefaultAsync(e => e.GlobalEventId == globalEventId);
            Assert.Null(applied);
        }

        await Task.Delay(TimeSpan.FromSeconds(5));
        await fixture.StartHubAsync();

        // All five must arrive, exactly once each, with no corruption —
        // the forwarder must notice the leaf's reconnection and drain the
        // backlog on its own, with no help from the test.
        foreach (var globalEventId in duringOutage)
        {
            var applied = await WaitUntilAppliedAsync(serverDb, globalEventId, timeout: TimeSpan.FromSeconds(60));
            Assert.NotNull(applied);
        }

        output.WriteLine("All events from the outage window were applied after reconnect.");

        var appliedCountForStream = await serverDb.Events.CountAsync(e => e.StreamId == streamId);
        Assert.Equal(6, appliedCountForStream); // 1 before + 5 during outage, no duplicates

        await forwarderCts.CancelAsync();
        await responderCts.CancelAsync();
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

    private async Task<(DaemonHarness Harness, EventStoreDbContext Db)> BuildDaemonSideAsync(string testId)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-sync-daemon-{testId}.db");
        _dbPaths.Add(dbPath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={dbPath}");
        services.AddSingleton<HlcGenerator>();
        services.Configure<DaemonOptions>(o => o.SiteId = $"daemon-{testId}");
        services.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = fixture.LeafClientUrl;
            o.StreamName = $"DAEMON_EVENTS_{testId}";
            o.ConsumerName = $"FORWARDER_{testId}";
            o.SubjectPrefix = $"events{testId}";
            o.ApplyRequestSubject = $"server.apply.request.{testId}";
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddScoped<LocalEventWriter>();
        services.AddSingleton<DaemonJetStreamSetup>();
        services.AddSingleton<EventForwarder>();

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        var natsOptions = provider.GetRequiredService<IOptions<DaemonNatsOptions>>().Value;
        var harness = new DaemonHarness(
            Writer: provider.GetRequiredService<LocalEventWriter>(),
            JetStreamSetup: provider.GetRequiredService<DaemonJetStreamSetup>(),
            Forwarder: provider.GetRequiredService<EventForwarder>(),
            JetStream: provider.GetRequiredService<NatsJSContext>(),
            NatsOptions: natsOptions);

        return (harness, provider.GetRequiredService<EventStoreDbContext>());
    }

    private async Task<(ServerHarness Harness, EventStoreDbContext Db)> BuildServerSideAsync(string testId)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-sync-server-{testId}.db");
        _dbPaths.Add(dbPath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={dbPath}");
        services.Configure<ServerNatsOptions>(o =>
        {
            o.Url = fixture.HubClientUrl;
            o.ApplyRequestSubject = $"server.apply.request.{testId}";
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<ServerNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddSingleton<ApplyResponder>();

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        var harness = new ServerHarness(Responder: provider.GetRequiredService<ApplyResponder>());
        return (harness, provider.GetRequiredService<EventStoreDbContext>());
    }

    private sealed record DaemonHarness(
        LocalEventWriter Writer,
        DaemonJetStreamSetup JetStreamSetup,
        EventForwarder Forwarder,
        NatsJSContext JetStream,
        DaemonNatsOptions NatsOptions);

    private sealed record ServerHarness(ApplyResponder Responder);

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
