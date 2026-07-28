using SyncMesh.MeshMonitor.Api;

namespace SyncMesh.MeshMonitor.Tests;

// Unit tests for TopologyStore's fold logic in isolation — the in-memory
// topology snapshot ADR-0005 describes, no NATS or SignalR needed. See
// ARCHITECTURE.md's note on why this backend gets unit tests only, not a
// full BDD suite (matching the Order Book demo's own precedent).
public sealed class TopologyStoreTests
{
    private static TopologyNode MakeNode(string kind = "daemon", string siteId = "site-a", string instanceId = "daemon-a") =>
        new(kind, siteId, instanceId, DateTimeOffset.UtcNow, new { });

    [Fact]
    public void Upsert_ThenSnapshot_ReturnsTheNode()
    {
        var store = new TopologyStore();

        store.Upsert(MakeNode());

        var node = Assert.Single(store.Snapshot());
        Assert.Equal("site-a", node.SiteId);
        Assert.Equal("daemon-a", node.InstanceId);
    }

    [Fact]
    public void Upsert_SameSiteAndInstance_ReplacesThePreviousNode()
    {
        var store = new TopologyStore();
        store.Upsert(MakeNode() with { LastSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });

        var latest = MakeNode();
        store.Upsert(latest);

        var node = Assert.Single(store.Snapshot());
        Assert.Equal(latest.LastSeenUtc, node.LastSeenUtc);
    }

    [Fact]
    public void Upsert_DifferentSites_KeepsBothNodesIndependent()
    {
        // The whole point of the {SiteId}:{InstanceId} key: a two-site
        // mesh's nodes never collide even if InstanceId happens to match.
        var store = new TopologyStore();

        store.Upsert(MakeNode(siteId: "site-a", instanceId: "server-1"));
        store.Upsert(MakeNode(kind: "server", siteId: "site-b", instanceId: "server-1"));

        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void Snapshot_Empty_ReturnsEmptyNotNull()
    {
        var store = new TopologyStore();

        Assert.Empty(store.Snapshot());
    }
}
