using SyncMesh.MeshMonitor.Api;

const string DevCorsPolicy = "DevCors";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<MeshMonitorApiOptions>()
    .Bind(builder.Configuration.GetSection(MeshMonitorApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ITopologyStore, TopologyStore>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<MonitorSubscriber>();

// Vite's dev server runs on its own origin — only needed while developing
// the frontend against `npm run dev`; the built app is served same-origin
// from wwwroot in every other case, so this never runs outside Development.
builder.Services.AddCors(options => options.AddPolicy(DevCorsPolicy, policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCorsPolicy);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/topology", (ITopologyStore store) => Results.Ok(store.Snapshot()));
app.MapHub<MeshMonitorHub>("/hubs/mesh-monitor");

// SPA fallback — once web/mesh-monitor's build output is copied into
// wwwroot, any route not matched above (client-side vue-router paths)
// serves index.html instead of 404ing.
app.MapFallbackToFile("index.html");

app.Run();
