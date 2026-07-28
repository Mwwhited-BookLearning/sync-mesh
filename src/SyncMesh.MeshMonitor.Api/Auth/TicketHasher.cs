using System.Security.Cryptography;
using System.Text;

namespace SyncMesh.MeshMonitor.Api.Auth;

// The one computation both sides must agree on: POST /auth/ticket
// computes this once (server-side) to know which key to store the
// caller's identity under; the client computes the identical value
// itself afterward (it already holds both inputs) before presenting it
// as the "ticket" query parameter on a later request. The server never
// transmits this hashed value itself — only the raw ticketId, which is
// useless alone without the client's own one-time secret to combine it
// with. See docs/adr/0009-ticket-based-signalr-auth.md.
public static class TicketHasher
{
    public static string Compute(string oneTimeSecret, string ticketId)
    {
        var key = Encoding.UTF8.GetBytes(oneTimeSecret);
        var message = Encoding.UTF8.GetBytes(ticketId);
        return Convert.ToHexString(HMACSHA256.HashData(key, message));
    }
}
