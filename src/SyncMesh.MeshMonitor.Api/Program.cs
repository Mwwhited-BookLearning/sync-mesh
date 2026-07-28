using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SyncMesh.MeshMonitor.Api;
using SyncMesh.MeshMonitor.Api.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<MeshMonitorApiOptions>()
    .Bind(builder.Configuration.GetSection(MeshMonitorApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<MeshMonitorAuthOptions>()
    .Bind(builder.Configuration.GetSection(MeshMonitorAuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<TicketOptions>()
    .Bind(builder.Configuration.GetSection(TicketOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ITopologyStore, TopologyStore>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<MonitorSubscriber>();

// Bearer + ticket auth — see docs/adr/0009-ticket-based-signalr-auth.md.
// This dashboard doesn't issue bearer tokens itself; some external
// issuer signs JWTs with the configured symmetric key. The ticket scheme
// exists purely so that value never has to appear in a URL (the
// SignalR/WebSocket connection this dashboard needs can't set a custom
// Authorization header at all).
builder.Services.AddSingleton<ITicketStore, TicketStore>();
builder.Services.AddHostedService<TicketCleanupService>();
builder.Services
    .AddAuthentication()
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var authOptions = builder.Configuration.GetSection(MeshMonitorAuthOptions.SectionName).Get<MeshMonitorAuthOptions>()
            ?? new MeshMonitorAuthOptions();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(authOptions.SigningKey)),
            ValidateIssuer = authOptions.Issuer is not null,
            ValidIssuer = authOptions.Issuer,
            ValidateAudience = authOptions.Audience is not null,
            ValidAudience = authOptions.Audience,
        };
    })
    .AddScheme<TicketAuthenticationOptions, TicketAuthenticationHandler>(TicketAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapTicketEndpoints();
app.MapGet("/api/topology", (ITopologyStore store) => Results.Ok(store.Snapshot()))
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(AuthSchemeNames.BearerOrTicket.Split(','))
        .RequireAuthenticatedUser());
app.MapHub<MeshMonitorHub>("/hubs/mesh-monitor");

// SPA fallback — once web/mesh-monitor's build output is copied into
// wwwroot, any route not matched above (client-side vue-router paths)
// serves index.html instead of 404ing.
app.MapFallbackToFile("index.html");

app.Run();
