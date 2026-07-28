using SyncMesh.MeshMonitor.Api.Auth;

namespace SyncMesh.MeshMonitor.Tests;

// TicketHasher.Compute must be a pure, deterministic function of its two
// inputs — both /auth/ticket (server-side, once) and the client
// (independently, later) compute it and must always agree.
public sealed class TicketHasherTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var first = TicketHasher.Compute("a-long-enough-secret-value", "ticket-1");
        var second = TicketHasher.Compute("a-long-enough-secret-value", "ticket-1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DifferentSecrets_ProduceDifferentHashes()
    {
        var first = TicketHasher.Compute("secret-one-long-enough", "ticket-1");
        var second = TicketHasher.Compute("secret-two-long-enough", "ticket-1");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_DifferentTicketIds_ProduceDifferentHashes()
    {
        var first = TicketHasher.Compute("a-long-enough-secret-value", "ticket-1");
        var second = TicketHasher.Compute("a-long-enough-secret-value", "ticket-2");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_KnowingOnlyTheHash_DoesNotRevealTheTicketId()
    {
        // The whole point: the hash alone (what ends up in a URL/log) is
        // not the ticketId in disguise — it's a keyed function of it.
        var ticketId = "ticket-1";
        var hash = TicketHasher.Compute("a-long-enough-secret-value", ticketId);

        Assert.NotEqual(ticketId, hash);
        Assert.DoesNotContain(ticketId, hash, StringComparison.OrdinalIgnoreCase);
    }
}
