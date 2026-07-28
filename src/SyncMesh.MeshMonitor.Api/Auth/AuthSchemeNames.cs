using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace SyncMesh.MeshMonitor.Api.Auth;

// Every protected endpoint accepts either a real bearer token OR a
// redeemed ticket — one shared constant so both never drift apart.
public static class AuthSchemeNames
{
    public const string BearerOrTicket = $"{JwtBearerDefaults.AuthenticationScheme},{TicketAuthenticationHandler.SchemeName}";
}
