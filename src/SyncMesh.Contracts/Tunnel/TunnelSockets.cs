using System.Net;
using System.Net.Sockets;

namespace SyncMesh.Contracts.Tunnel;

public static class TunnelSockets
{
    // Dual-stack (IPv4 + IPv6) listener — avoids any dependency on which
    // address family "localhost" happens to resolve to first on a given
    // machine. A listener bound to IPAddress.Any (IPv4-only) can silently
    // add connect latency (or fail outright within a short attempt
    // timeout, as observed in this project's own BDD run) if "localhost"
    // resolves to ::1 first and nothing IPv6 is listening.
    public static TcpListener CreateDualStackListener(int port)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        return listener;
    }
}
