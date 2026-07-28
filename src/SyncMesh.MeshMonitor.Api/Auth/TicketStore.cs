using System.Collections.Concurrent;
using System.Security.Claims;

namespace SyncMesh.MeshMonitor.Api.Auth;

public interface ITicketStore
{
    void Store(string hashedTicket, ClaimsPrincipal principal, DateTimeOffset expiresAtUtc);

    // Redeeming always removes the entry, whether or not it was still
    // valid — a ticket is one-time-use by construction; there is no
    // "peek without consuming."
    bool TryRedeem(string hashedTicket, out ClaimsPrincipal? principal);

    void PurgeExpired(DateTimeOffset nowUtc);
}

// In-memory only, no durability — same reasoning as ITopologyStore: a
// ticket is meant to live for under a couple of minutes (TicketOptions
// .Ttl), so a restart losing outstanding tickets just means those
// specific in-flight redemptions fail and the client re-requests one,
// not a real correctness problem.
public sealed class TicketStore : ITicketStore
{
    private sealed record Entry(ClaimsPrincipal Principal, DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new();

    public void Store(string hashedTicket, ClaimsPrincipal principal, DateTimeOffset expiresAtUtc) =>
        _tickets[hashedTicket] = new Entry(principal, expiresAtUtc);

    public bool TryRedeem(string hashedTicket, out ClaimsPrincipal? principal)
    {
        principal = null;

        if (!_tickets.TryRemove(hashedTicket, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return false; // already removed above — an expired ticket is single-use-consumed too
        }

        principal = entry.Principal;
        return true;
    }

    public void PurgeExpired(DateTimeOffset nowUtc)
    {
        foreach (var (hashedTicket, entry) in _tickets)
        {
            if (entry.ExpiresAtUtc < nowUtc)
            {
                _tickets.TryRemove(hashedTicket, out _);
            }
        }
    }
}
