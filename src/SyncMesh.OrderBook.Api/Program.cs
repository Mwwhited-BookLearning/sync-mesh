using SyncMesh.EventStore;
using SyncMesh.OrderBook.Api;
using SyncMesh.OrderBook.Api.Commands;
using SyncMesh.OrderBook.Api.Queries;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<OrderBookApiOptions>()
    .Bind(builder.Configuration.GetSection(OrderBookApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Read-only from this API's point of view — deliberately just ONE of the
// two demo servers' databases (see OrderBookProjector's doc comment for
// why that's the actual point, not a shortcut). SQLite here because this
// dev topology's server tier uses SQLite too (see ADR-0001's Amendment) —
// pointed at the exact same file serverhost-a's own EventStoreDbContext
// migrates and writes to.
var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? throw new InvalidOperationException("Missing configuration value 'ConnectionStrings:EventStore'.");
builder.Services.AddSqliteEventStore(connectionString);

builder.Services.AddSingleton<IOrderBookStore, OrderBookStore>();
builder.Services.AddHostedService<OrderBookProjector>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOrderCommands();
app.MapOrderQueries();

app.Run();
