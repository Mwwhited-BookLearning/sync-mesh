using System.Security.Claims;
using SyncMesh.MeshMonitor.Api.Auth;

namespace SyncMesh.MeshMonitor.Tests;

// Unit tests for TicketStore's redemption semantics — the actual security
// property this feature depends on (single-use, expiry) — no HTTP/auth
// pipeline needed. See docs/adr/0009-ticket-based-signalr-auth.md.
public sealed class TicketStoreTests
{
    private static ClaimsPrincipal MakePrincipal(string name = "alice") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "Bearer"));

    [Fact]
    public void Store_ThenRedeem_ReturnsTheStoredPrincipal()
    {
        var store = new TicketStore();
        var principal = MakePrincipal();

        store.Store("hash-1", principal, DateTimeOffset.UtcNow.AddMinutes(1));

        var redeemed = store.TryRedeem("hash-1", out var result);

        Assert.True(redeemed);
        Assert.Same(principal, result);
    }

    [Fact]
    public void Redeem_IsSingleUse()
    {
        var store = new TicketStore();
        store.Store("hash-1", MakePrincipal(), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(store.TryRedeem("hash-1", out _));
        Assert.False(store.TryRedeem("hash-1", out var second));
        Assert.Null(second);
    }

    [Fact]
    public void Redeem_UnknownHash_Fails()
    {
        var store = new TicketStore();

        Assert.False(store.TryRedeem("never-issued", out var principal));
        Assert.Null(principal);
    }

    [Fact]
    public void Redeem_ExpiredTicket_FailsAndConsumesIt()
    {
        var store = new TicketStore();
        store.Store("hash-1", MakePrincipal(), DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.False(store.TryRedeem("hash-1", out var principal));
        Assert.Null(principal);

        // An expired ticket is gone either way — a second attempt (e.g. a
        // retry) must not somehow succeed on stale state.
        Assert.False(store.TryRedeem("hash-1", out _));
    }

    [Fact]
    public void PurgeExpired_RemovesOnlyExpiredEntries()
    {
        var store = new TicketStore();
        var now = DateTimeOffset.UtcNow;
        store.Store("expired", MakePrincipal(), now.AddSeconds(-1));
        store.Store("still-valid", MakePrincipal(), now.AddMinutes(1));

        store.PurgeExpired(now);

        Assert.False(store.TryRedeem("expired", out _));
        Assert.True(store.TryRedeem("still-valid", out _));
    }
}
