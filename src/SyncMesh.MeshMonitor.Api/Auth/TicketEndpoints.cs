using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace SyncMesh.MeshMonitor.Api.Auth;

public sealed record TicketRequest(string OneTimeSecret);

public sealed record TicketResponse(string TicketId);

public static class TicketEndpoints
{
    // A generous-but-bounded floor: strong enough that brute-forcing the
    // client-computed hash isn't meaningfully easier than guessing the
    // (128-bit random) ticketId itself, without dictating exactly how the
    // client generates it.
    private const int MinSecretLength = 16;

    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        // Requires a real bearer token via the Authorization header — see
        // docs/adr/0009-ticket-based-signalr-auth.md. Restricted to the
        // "Bearer" scheme explicitly: a ticket shouldn't be usable to mint
        // another ticket, only a real bearer token should.
        app.MapPost("/auth/ticket", (TicketRequest request, HttpContext context, ITicketStore store, IOptions<TicketOptions> options) =>
        {
            if (string.IsNullOrWhiteSpace(request.OneTimeSecret) || request.OneTimeSecret.Length < MinSecretLength)
            {
                return Results.BadRequest($"oneTimeSecret is required and must be at least {MinSecretLength} characters.");
            }

            // Server-generated, unguessable (128 bits) — returned as-is to
            // the client. Alone it grants nothing; only combined with the
            // secret the client already holds (never sent back to it) does
            // it become the value the ticket middleware will accept.
            var ticketId = Guid.NewGuid().ToString("N");
            var hashedTicket = TicketHasher.Compute(request.OneTimeSecret, ticketId);
            var expiresAtUtc = DateTimeOffset.UtcNow.Add(options.Value.Ttl);

            store.Store(hashedTicket, context.User, expiresAtUtc);

            return Results.Ok(new TicketResponse(ticketId));
        })
        .RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());

        return app;
    }
}
