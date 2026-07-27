using SyncMesh.MeshMonitor.Api;

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

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/topology", (ITopologyStore store) => Results.Ok(store.Snapshot()));
app.MapHub<MeshMonitorHub>("/hubs/mesh-monitor");

// SPA fallback — once web/mesh-monitor's build output is copied into
// wwwroot, any route not matched above (client-side vue-router paths)
// serves index.html instead of 404ing.
app.MapFallbackToFile("index.html");

app.Run();
