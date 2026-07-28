using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SyncMesh.MeshMonitor.Api.Auth;

public sealed class TicketAuthenticationOptions : AuthenticationSchemeOptions;

// Redeems a hashed ticket (see TicketHasher) in place of a bearer token —
// this is the "middleware" half of the ticket exchange described in
// docs/adr/0009-ticket-based-signalr-auth.md. Implemented as a real
// ASP.NET Core authentication scheme (not raw middleware poking
// HttpContext.User before UseAuthentication runs) so it composes
// correctly with [Authorize]/RequireAuthorization and with SignalR's own
// authentication integration, the same way JwtBearer does.
public sealed class TicketAuthenticationHandler(
    IOptionsMonitor<TicketAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITicketStore store) : AuthenticationHandler<TicketAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "Ticket";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hashedTicket = ExtractHashedTicket();

        if (string.IsNullOrEmpty(hashedTicket))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!store.TryRedeem(hashedTicket, out var principal) || principal is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Ticket is invalid, expired, or already used."));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    private string? ExtractHashedTicket()
    {
        // Query string first — the whole reason this scheme exists: a
        // browser's WebSocket handshake (SignalR) can't set a custom
        // Authorization header at all, only a URL. Two accepted param
        // names: "access_token" is what @microsoft/signalr's own
        // accessTokenFactory option sends automatically before every
        // (re)connection attempt (not configurable client-side, hence
        // reusing it here rather than inventing a third mechanism just to
        // rename a query parameter) — despite the name, the *value* is
        // still this handler's hashed ticket, never the real bearer
        // token. "ticket" is accepted too, for any non-SignalR caller
        // that would rather use the more descriptive name.
        var fromQuery = Request.Query["access_token"].FirstOrDefault()
            ?? Request.Query["ticket"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        // Authorization header form, for parity — "in place of a bearer
        // token for any request" — a non-browser caller that can set
        // headers has no reason to put even this short-lived value in a
        // URL if it doesn't have to.
        var header = Request.Headers.Authorization.FirstOrDefault();
        var prefix = $"{SchemeName} ";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..]
            : null;
    }
}
