using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;
using Xunit.Abstractions;

namespace SyncMesh.Sync.Tests;

// Proves the Phase 3 exit criteria: server-mesh replication converges
// regardless of topology shape (2-node direct peers, 3-node transitive
// relay through a designated gateway), survives an extended peer outage,
// and does so with no synchronous coordination between peers — see
// docs/00-design-document.md §4.4 and ADR-0002's 2026-07-23 (Phase 3)
// Amendment. Real nats-server containers throughout, per this project's
// "prove it against real infrastructure" convention (ADR-0002 Amendment,
// Sync.Tests for Phase 2).
public sealed class ServerMeshReconciliationTests(ServerMeshFixture fixture, ITestOutputHelper output)
    : IClassFixture<ServerMeshFixture>, IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<string> _dbPaths = [];
    private readonly HlcGenerator _hlc = new();

    [Fact]
    public async Task TwoNodes_EachConvergesOnEventsTheOtherOriginates_NoSynchronousCoordination()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var applySubject = $"server.apply.request.{testId}";

        var nodeA = await fixture.CreateNodeAsync($"a-{testId}");
        var nodeB = await fixture.CreateNodeAsync($"b-{testId}");

        var a = await BuildServerNodeAsync(testId, "node-a", nodeA.ClientUrl, applySubject,
            peers: [("node-b", nodeB.ClientUrl, applySubject)]);
        var b = await BuildServerNodeAsync(testId, "node-b", nodeB.ClientUrl, applySubject,
            peers: [("node-a", nodeA.ClientUrl, applySubject)]);

        await StartNodeAsync(a);
        await StartNodeAsync(b);

        // Each origin owns its own stream — StreamVersion is scoped "at the
        // originating site" (docs/06-data-model.md §3), so two independent
        // origins never share a (StreamId, StreamVersion) pair in practice.
        var streamIdA = Guid.NewGuid();
        var streamIdB = Guid.NewGuid();
        var fromA = await AppendAsSimulatedDaemonAsync(a, "node-a", streamIdA, 1, "FromA");
        var fromB = await AppendAsSimulatedDaemonAsync(b, "node-b", streamIdB, 1, "FromB");

        Assert.NotNull(await WaitUntilAppliedAsync(b.Db, fromA));
        Assert.NotNull(await WaitUntilAppliedAsync(a.Db, fromB));

        // No duplicates on either side despite the gossip bounce (each
        // node also re-publishes what it applies from its peer onto its
        // own MESH_OUTBOUND — the origin's own no-op path is what stops it
        // from going any further).
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(2, await a.Db.Events.CountAsync());
        Assert.Equal(2, await b.Db.Events.CountAsync());

        output.WriteLine("Both nodes converged on both events with no duplicates.");

        a.Cts.Cancel();
        b.Cts.Cancel();
    }

    [Fact]
    public async Task ThreeNodes_TransitiveRelayThroughADesignatedGateway_BothEndsConverge()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var applySubject = $"server.apply.request.{testId}";

        // A and C only peer with B (the designated gateway) — neither peers
        // directly with the other. This is the "servers within a site are
        // fully meshed; cross-site links use a limited gateway" shape.
        var nodeA = await fixture.CreateNodeAsync($"a-{testId}");
        var nodeB = await fixture.CreateNodeAsync($"b-{testId}");
        var nodeC = await fixture.CreateNodeAsync($"c-{testId}");

        var a = await BuildServerNodeAsync(testId, "node-a", nodeA.ClientUrl, applySubject,
            peers: [("node-b", nodeB.ClientUrl, applySubject)]);
        var b = await BuildServerNodeAsync(testId, "node-b", nodeB.ClientUrl, applySubject,
            peers: [("node-a", nodeA.ClientUrl, applySubject), ("node-c", nodeC.ClientUrl, applySubject)]);
        var c = await BuildServerNodeAsync(testId, "node-c", nodeC.ClientUrl, applySubject,
            peers: [("node-b", nodeB.ClientUrl, applySubject)]);

        await StartNodeAsync(a);
        await StartNodeAsync(b);
        await StartNodeAsync(c);

        var streamIdA = Guid.NewGuid();
        var streamIdC = Guid.NewGuid();
        var fromA = await AppendAsSimulatedDaemonAsync(a, "node-a", streamIdA, 1, "FromA");
        var fromC = await AppendAsSimulatedDaemonAsync(c, "node-c", streamIdC, 1, "FromC");

        // A's event must reach C, and C's event must reach A, despite
        // neither having a direct peer connection — B must relay both,
        // including the one it merely received (not originated).
        Assert.NotNull(await WaitUntilAppliedAsync(c.Db, fromA, timeout: TimeSpan.FromSeconds(30)));
        Assert.NotNull(await WaitUntilAppliedAsync(a.Db, fromC, timeout: TimeSpan.FromSeconds(30)));
        Assert.NotNull(await WaitUntilAppliedAsync(b.Db, fromA));
        Assert.NotNull(await WaitUntilAppliedAsync(b.Db, fromC));

        output.WriteLine("Both leaf nodes converged transitively through the designated gateway.");

        a.Cts.Cancel();
        b.Cts.Cancel();
        c.Cts.Cancel();
    }

    [Fact]
    public async Task ExtendedPeerOutage_AllEventsFromBothSidesEventuallyConverge_NoLossNoDuplication()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var applySubject = $"server.apply.request.{testId}";

        var nodeA = await fixture.CreateNodeAsync($"a-{testId}");
        var nodeB = await fixture.CreateNodeAsync($"b-{testId}");

        var a = await BuildServerNodeAsync(testId, "node-a", nodeA.ClientUrl, applySubject,
            peers: [("node-b", nodeB.ClientUrl, applySubject)]);
        var b = await BuildServerNodeAsync(testId, "node-b", nodeB.ClientUrl, applySubject,
            peers: [("node-a", nodeA.ClientUrl, applySubject)]);

        await StartNodeAsync(a);
        await StartNodeAsync(b);

        var streamId = Guid.NewGuid();
        var nextVersion = 1L;
        var beforeOutage = await AppendAsSimulatedDaemonAsync(a, "node-a", streamId, nextVersion++, "BeforeOutage");
        Assert.NotNull(await WaitUntilAppliedAsync(b.Db, beforeOutage));

        // Extended outage: stop node B's container entirely (not just a
        // network blip), same technique as Phase 2's hub stop/restart test.
        await nodeB.StopAsync();

        var duringOutage = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            duringOutage.Add(await AppendAsSimulatedDaemonAsync(a, "node-a", streamId, nextVersion++, $"DuringOutage{i}"));
        }

        foreach (var globalEventId in duringOutage)
        {
            Assert.Null(await b.Db.Events.FirstOrDefaultAsync(e => e.GlobalEventId == globalEventId));
        }

        await Task.Delay(TimeSpan.FromSeconds(5));
        await nodeB.StartAsync();

        foreach (var globalEventId in duringOutage)
        {
            Assert.NotNull(await WaitUntilAppliedAsync(b.Db, globalEventId, timeout: TimeSpan.FromSeconds(60)));
        }

        var appliedCountForStream = await b.Db.Events.CountAsync(e => e.StreamId == streamId);
        Assert.Equal(6, appliedCountForStream); // 1 before + 5 during outage, no duplicates

        output.WriteLine("All events from the outage window converged after B's reconnect.");

        a.Cts.Cancel();
        b.Cts.Cancel();
    }

    private async Task<Guid> AppendAsSimulatedDaemonAsync(ServerNodeHarness node, string originSiteId, Guid streamId, long streamVersion, string eventType)
    {
        var envelope = new EventEnvelope
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = streamId,
            StreamVersion = streamVersion,
            OriginSiteId = originSiteId,
            Hlc = _hlc.Next(),
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = eventType,
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        };

        var reply = await node.Connection.RequestAsync<byte[], byte[]>(
            node.ApplyRequestSubject,
            JsonSerializer.SerializeToUtf8Bytes(envelope),
            cancellationToken: CancellationToken.None);

        Assert.NotNull(reply.Data);
        return envelope.GlobalEventId;
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

    private static async Task StartNodeAsync(ServerNodeHarness node)
    {
        await node.MeshSetup.StartAsync(CancellationToken.None);
        _ = node.Responder.StartAsync(node.Cts.Token);
        _ = node.Forwarder.StartAsync(node.Cts.Token);
    }

    private async Task<ServerNodeHarness> BuildServerNodeAsync(
        string testId,
        string nodeAlias,
        string url,
        string applySubject,
        params (string SiteId, string Url, string ApplyRequestSubject)[] peers)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-mesh-{nodeAlias}-{testId}.db");
        _dbPaths.Add(dbPath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={dbPath}");
        services.Configure<ServerNatsOptions>(o =>
        {
            o.Url = url;
            o.ApplyRequestSubject = applySubject;
        });
        services.Configure<ServerMeshOptions>(o =>
        {
            o.OutboundStreamName = $"MESH_OUTBOUND_{nodeAlias}_{testId}".Replace('-', '_').ToUpperInvariant();
            o.OutboundSubjectPrefix = $"mesh.outbound.{nodeAlias}.{testId}";
            o.Peers = peers.Select(p => new MeshPeerOptions { SiteId = p.SiteId, Url = p.Url, ApplyRequestSubject = p.ApplyRequestSubject }).ToList();
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<ServerNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddSingleton<ServerMeshSetup>();
        services.AddSingleton<ApplyResponder>();
        services.AddSingleton<MeshForwarder>();

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        return new ServerNodeHarness(
            Db: provider.GetRequiredService<EventStoreDbContext>(),
            Connection: provider.GetRequiredService<NatsConnection>(),
            ApplyRequestSubject: applySubject,
            MeshSetup: provider.GetRequiredService<ServerMeshSetup>(),
            Responder: provider.GetRequiredService<ApplyResponder>(),
            Forwarder: provider.GetRequiredService<MeshForwarder>(),
            Cts: new CancellationTokenSource());
    }

    private sealed record ServerNodeHarness(
        EventStoreDbContext Db,
        NatsConnection Connection,
        string ApplyRequestSubject,
        ServerMeshSetup MeshSetup,
        ApplyResponder Responder,
        MeshForwarder Forwarder,
        CancellationTokenSource Cts);

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
