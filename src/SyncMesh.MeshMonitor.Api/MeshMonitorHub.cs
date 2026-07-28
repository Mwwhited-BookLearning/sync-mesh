using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SyncMesh.MeshMonitor.Api.Auth;

namespace SyncMesh.MeshMonitor.Api;

// Server-push only for now — the browser client never calls back into
// this hub, it just listens for "NodeUpdated" (see MonitorSubscriber).
// Requires either a real bearer token or a redeemed ticket — see
// docs/adr/0009-ticket-based-signalr-auth.md. A browser's WebSocket
// handshake can't carry a custom Authorization header at all, which is
// exactly why the ticket scheme (a short-lived, single-use, query-string-
// safe value) exists instead of putting the real bearer token in this
// hub's connection URL.
[Authorize(AuthenticationSchemes = AuthSchemeNames.BearerOrTicket)]
public sealed class MeshMonitorHub : Hub;
