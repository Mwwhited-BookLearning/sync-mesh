using System.Collections.Concurrent;

namespace SyncMesh.MeshMonitor.Api;

// A single mesh node's latest self-reported telemetry — Status is either a
// SyncMesh.Contracts.DaemonStatus or ServerStatus (discriminated by
// NodeKind on the way in; System.Text.Json serializes this back out to
// clients using the object's actual runtime type/shape, not a plain
// object, since no source-generated serializer is in play here).
public sealed record TopologyNode(string NodeKind, string SiteId, string InstanceId, DateTimeOffset LastSeenUtc, object Status);

public interface ITopologyStore
{
    void Upsert(TopologyNode node);
    IReadOnlyCollection<TopologyNode> Snapshot();
}

// In-memory only — this is a live view of currently-reachable telemetry,
// not a system of record. Restarting the API just means it re-learns the
// topology from the next round of monitor.> ticks (every node publishes
// on its own interval regardless of whether anyone's listening).
public sealed class TopologyStore : ITopologyStore
{
    private readonly ConcurrentDictionary<string, TopologyNode> _nodes = new();

    public void Upsert(TopologyNode node) => _nodes[$"{node.SiteId}:{node.InstanceId}"] = node;

    public IReadOnlyCollection<TopologyNode> Snapshot() => _nodes.Values.ToList();
}
