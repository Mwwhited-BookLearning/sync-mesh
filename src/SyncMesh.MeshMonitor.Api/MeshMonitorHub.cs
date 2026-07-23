using Microsoft.AspNetCore.SignalR;

namespace SyncMesh.MeshMonitor.Api;

// Server-push only for now — the browser client never calls back into
// this hub, it just listens for "NodeUpdated" (see MonitorSubscriber).
public sealed class MeshMonitorHub : Hub;
