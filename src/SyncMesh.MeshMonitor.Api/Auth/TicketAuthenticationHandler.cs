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

    private string? ExtractHashedTicket() =>
        ExtractHashedTicket(Request.Query["access_token"].FirstOrDefault() ?? Request.Query["ticket"].FirstOrDefault(),
            Request.Headers.Authorization.FirstOrDefault());

    // Query string first — the whole reason this scheme exists: a
    // browser's WebSocket handshake (SignalR) can't set a custom
    // Authorization header at all, only a URL. Two accepted param names:
    // "access_token" is what @microsoft/signalr's own accessTokenFactory
    // option sends automatically before every (re)connection attempt (not
    // configurable client-side, hence reusing it here rather than
    // inventing a third mechanism just to rename a query parameter) —
    // despite the name, the *value* is still this handler's hashed
    // ticket, never the real bearer token. "ticket" is accepted too, for
    // any non-SignalR caller that would rather use the more descriptive
    // name.
    //
    // Header form accepts BOTH "Ticket <hash>" (the documented form for a
    // non-SignalR caller that can set headers but wants to avoid even
    // this short-lived value in a URL) and "Bearer <hash>" — the latter
    // is not optional: @microsoft/signalr's AccessTokenHttpClient sends
    // `Authorization: Bearer <accessTokenFactory-value>` on every HTTP
    // request it makes through the connection, including the /negotiate
    // request, before a WebSocket even exists to put the value in a URL
    // (confirmed directly in node_modules/@microsoft/signalr/dist/esm/
    // AccessTokenHttpClient.js — _setAuthorizationHeader always uses
    // "Bearer", never configurable). Without accepting "Bearer" here,
    // /negotiate 401s on every connection attempt and the hub can never
    // connect — the ticket's hash value never actually collides with a
    // real JWT (TryRedeem just fails and this scheme no-results, letting
    // the JwtBearer scheme in the same policy evaluate it as a JWT
    // instead), so accepting both prefixes is safe.
    internal static string? ExtractHashedTicket(string? fromQuery, string? authorizationHeader)
    {
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        if (authorizationHeader is null)
        {
            return null;
        }

        foreach (var prefix in TicketHeaderPrefixes)
        {
            if (authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return authorizationHeader[prefix.Length..];
            }
        }

        return null;
    }

    private static readonly string[] TicketHeaderPrefixes = [$"{SchemeName} ", "Bearer "];
}
