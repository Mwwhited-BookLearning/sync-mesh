using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/event-ordering-and-idempotency.feature — the
// server-mesh-facing scenarios: duplicate delivery, and reconciliation
// after an extended partition. Reuses the same real-container, real-
// ApplyResponder/MeshForwarder pattern validated in
// SyncMesh.Sync.Tests.ServerMeshReconciliationTests, trimmed to what these
// two scenarios need. See ADR-0002's 2026-07-23 (Phase 3) Amendment.
public sealed class EventOrderingMeshContext : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<string> _dbPaths = [];
    private readonly List<IContainer> _containers = [];
    private INetwork? _network;
    private readonly HlcGenerator _hlc = new();

    public bool? LastApplyReplyWasOk { get; private set; }

    // Split from StartServerAsync because peer configuration needs every
    // node's URL known up front — a two-node mesh has a chicken-and-egg
    // dependency (A's config needs B's URL and vice versa) that a single
    // "create and configure in one call" method can't resolve.
    public async Task<(string Url, IContainer Container)> PrepareContainerAsync(string alias)
    {
        _network ??= await CreateNetworkAsync();

        var config = $$"""
            port: 4222
            server_name: {{alias}}
            jetstream {
                store_dir: "/data"
            }
            """;
        var hostPort = GetFreeTcpPort();
        var container = new ContainerBuilder("nats:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(alias)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(hostPort, 4222)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await container.StartAsync();
        _containers.Add(container);

        return ($"nats://{container.Hostname}:{container.GetMappedPublicPort(4222)}", container);
    }

    public async Task<ServerNode> CreateServerAsync(string alias, params (string SiteId, string Url)[] peers)
    {
        var (url, container) = await PrepareContainerAsync(alias);
        return await StartServerAsync(alias, url, container, peers);
    }

    public async Task<ServerNode> StartServerAsync(string alias, string url, IContainer container, params (string SiteId, string Url)[] peers)
    {
        var applySubject = "server.apply.request";
        var testId = Guid.NewGuid().ToString("N")[..8];

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-ordering-{alias}-{testId}.db")}");
        services.Configure<ServerNatsOptions>(o =>
        {
            o.Url = url;
            o.ApplyRequestSubject = applySubject;
        });
        services.Configure<ServerMeshOptions>(o =>
        {
            o.OutboundStreamName = $"MESH_OUTBOUND_{alias}_{testId}".Replace('-', '_').ToUpperInvariant();
            o.OutboundSubjectPrefix = $"mesh.outbound.{alias}.{testId}";
            o.Peers = peers.Select(p => new MeshPeerOptions { SiteId = p.SiteId, Url = p.Url, ApplyRequestSubject = applySubject }).ToList();
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = url }));
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

        var cts = new CancellationTokenSource();
        var node = new ServerNode(
            Alias: alias,
            Url: url,
            ApplyRequestSubject: applySubject,
            Db: provider.GetRequiredService<EventStoreDbContext>(),
            Connection: provider.GetRequiredService<NatsConnection>(),
            MeshSetup: provider.GetRequiredService<ServerMeshSetup>(),
            Responder: provider.GetRequiredService<ApplyResponder>(),
            Forwarder: provider.GetRequiredService<MeshForwarder>(),
            Cts: cts,
            Container: container);

        await node.MeshSetup.StartAsync(CancellationToken.None);
        _ = node.Responder.StartAsync(cts.Token);
        _ = node.Forwarder.StartAsync(cts.Token);

        return node;
    }

    public EventEnvelope BuildEnvelope(string originSiteId, Guid streamId, long streamVersion, string eventType, HybridLogicalClock? explicitHlc = null) =>
        new()
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = streamId,
            StreamVersion = streamVersion,
            OriginSiteId = originSiteId,
            Hlc = explicitHlc ?? _hlc.Next(),
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = eventType,
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        };

    public async Task ApplyAsync(ServerNode node, EventEnvelope envelope)
    {
        var reply = await node.Connection.RequestAsync<byte[], byte[]>(
            node.ApplyRequestSubject,
            JsonSerializer.SerializeToUtf8Bytes(envelope),
            cancellationToken: CancellationToken.None);

        LastApplyReplyWasOk = reply.Data is not null && Encoding.UTF8.GetString(reply.Data) == "ok";
    }

    public Task<int> GetEventCountAsync(ServerNode node) => node.Db.Events.CountAsync();

    public Task<EventRecord?> FindEventAsync(ServerNode node, Guid globalEventId) =>
        node.Db.Events.FirstOrDefaultAsync(e => e.GlobalEventId == globalEventId);

    public async Task<List<EventRecord>> ReplayInHlcOrderAsync(ServerNode node) =>
        await node.Db.Events
            .OrderBy(e => e.HlcPhysicalTicks)
            .ThenBy(e => e.HlcLogicalCounter)
            .ToListAsync();

    public static async Task<EventRecord?> WaitUntilAppliedAsync(ServerNode node, Guid globalEventId, EventOrderingMeshContext context, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        EventRecord? applied = null;
        while (applied is null && DateTimeOffset.UtcNow < deadline)
        {
            applied = await context.FindEventAsync(node, globalEventId);
            if (applied is null)
            {
                await Task.Delay(200);
            }
        }
        return applied;
    }

    private static async Task<INetwork> CreateNetworkAsync()
    {
        var network = new NetworkBuilder().Build();
        await network.CreateAsync();
        return network;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

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

        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}

public sealed record ServerNode(
    string Alias,
    string Url,
    string ApplyRequestSubject,
    EventStoreDbContext Db,
    NatsConnection Connection,
    ServerMeshSetup MeshSetup,
    ApplyResponder Responder,
    MeshForwarder Forwarder,
    CancellationTokenSource Cts,
    IContainer Container)
{
    public Task StopContainerAsync() => Container.StopAsync();
    public Task StartContainerAsync() => Container.StartAsync();
}
