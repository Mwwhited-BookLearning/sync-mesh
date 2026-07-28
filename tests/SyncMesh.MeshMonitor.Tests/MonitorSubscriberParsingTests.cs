using System.Text.Json;
using SyncMesh.Contracts;
using SyncMesh.MeshMonitor.Api;

namespace SyncMesh.MeshMonitor.Tests;

// Unit tests for MonitorSubscriber.ParseNode — the discriminated-union
// parsing (NodeKind -> DaemonStatus or ServerStatus) that turns a raw
// monitor.> payload into a TopologyNode, in isolation from any actual
// NATS connection. ParseNode is internal — see AssemblyInfo.cs's
// InternalsVisibleTo.
public sealed class MonitorSubscriberParsingTests
{
    private static byte[] ToJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    [Fact]
    public void ParseNode_DaemonStatus_MapsToDaemonTopologyNode()
    {
        var status = new DaemonStatus
        {
            SiteId = "site-a",
            InstanceId = "daemon-a",
            TimestampUtc = DateTimeOffset.UtcNow,
            BufferedEventCount = 3,
            ConnectedToNearestServer = true,
            NearestServerUrl = "nats://localhost:4222",
            EventsForwardedCount = 42,
        };

        var node = MonitorSubscriber.ParseNode(ToJson(status));

        Assert.NotNull(node);
        Assert.Equal("daemon", node!.NodeKind);
        Assert.Equal("site-a", node.SiteId);
        Assert.Equal("daemon-a", node.InstanceId);
        var mapped = Assert.IsType<DaemonStatus>(node.Status);
        Assert.Equal(42, mapped.EventsForwardedCount);
    }

    [Fact]
    public void ParseNode_ServerStatus_MapsToServerTopologyNode()
    {
        var status = new ServerStatus
        {
            SiteId = "site-b",
            InstanceId = "server-b",
            TimestampUtc = DateTimeOffset.UtcNow,
            Url = "nats://localhost:4223",
            EventsAppliedCount = 7,
            ConfiguredPeers = [new PeerConnectionStatus { PeerSiteId = "site-a", PeerUrl = "nats://localhost:4222", EventsForwardedCount = 5 }],
        };

        var node = MonitorSubscriber.ParseNode(ToJson(status));

        Assert.NotNull(node);
        Assert.Equal("server", node!.NodeKind);
        Assert.Equal("site-b", node.SiteId);
        var mapped = Assert.IsType<ServerStatus>(node.Status);
        Assert.Equal("site-a", Assert.Single(mapped.ConfiguredPeers).PeerSiteId);
    }

    [Fact]
    public void ParseNode_UnknownNodeKind_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { NodeKind = "unknown-thing" });

        Assert.Null(MonitorSubscriber.ParseNode(payload));
    }

    [Fact]
    public void ParseNode_MissingNodeKind_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { SiteId = "site-a" });

        Assert.Null(MonitorSubscriber.ParseNode(payload));
    }

    [Fact]
    public void ParseNode_NullData_ReturnsNull()
    {
        Assert.Null(MonitorSubscriber.ParseNode(null));
    }
}
