using SyncMesh.MeshMonitor.Api.Auth;

namespace SyncMesh.MeshMonitor.Tests;

// Unit tests for TicketAuthenticationHandler.ExtractHashedTicket — the
// parsing logic behind the negotiate-401 fix in docs/adr/0009-ticket-
// based-signalr-auth.md's 2026-07-28 amendment. @microsoft/signalr always
// sends `Authorization: Bearer <accessTokenFactory-value>` on /negotiate
// (confirmed in node_modules/@microsoft/signalr/dist/esm/
// AccessTokenHttpClient.js), so "Bearer <hash>" must be accepted here
// alongside the documented "Ticket <hash>" header and the
// access_token/ticket query parameters.
public sealed class TicketAuthenticationHandlerTests
{
    [Fact]
    public void QueryParameter_TakesPrecedenceAndIsReturned()
    {
        var result = TicketAuthenticationHandler.ExtractHashedTicket("hash-from-query", "Ticket hash-from-header");

        Assert.Equal("hash-from-query", result);
    }

    [Fact]
    public void BearerHeader_IsAcceptedAsATicket()
    {
        // This is the exact case that broke every SignalR connection: the
        // negotiate request carries Authorization: Bearer <hash>, never
        // Authorization: Ticket <hash>.
        var result = TicketAuthenticationHandler.ExtractHashedTicket(null, "Bearer some-hashed-ticket");

        Assert.Equal("some-hashed-ticket", result);
    }

    [Fact]
    public void TicketHeader_IsStillAccepted()
    {
        var result = TicketAuthenticationHandler.ExtractHashedTicket(null, "Ticket some-hashed-ticket");

        Assert.Equal("some-hashed-ticket", result);
    }

    [Fact]
    public void HeaderPrefixMatch_IsCaseInsensitive()
    {
        var result = TicketAuthenticationHandler.ExtractHashedTicket(null, "bearer some-hashed-ticket");

        Assert.Equal("some-hashed-ticket", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public void NoUsableCredential_ReturnsNull(string? header)
    {
        var result = TicketAuthenticationHandler.ExtractHashedTicket(null, header);

        Assert.Null(result);
    }
}
