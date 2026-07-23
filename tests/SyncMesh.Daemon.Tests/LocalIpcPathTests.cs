using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Daemon.Tests;

public class LocalIpcPathTests
{
    [Fact]
    public async Task AppendEvent_PersistsAndAssignsGlobalEventIdAndHlc()
    {
        await using var host = await DaemonTestHost.CreateAsync();
        await host.StartAsync();
        var client = host.CreateClient();

        var streamId = Guid.NewGuid();
        var response = await client.AppendEventAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "TestEvent",
            PayloadJson = """{"value":1}""",
        });

        Assert.NotEqual(Guid.Empty, response.GlobalEventId);
        Assert.Equal(1, response.StreamVersion);
        Assert.True(response.HlcPhysicalTicks > 0);
    }

    [Fact]
    public async Task AppendEvent_AssignsSequentialStreamVersionsPerStream()
    {
        await using var host = await DaemonTestHost.CreateAsync();
        await host.StartAsync();
        var client = host.CreateClient();
        var streamId = Guid.NewGuid();

        var first = await client.AppendEventAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "TestEvent",
            PayloadJson = "{}",
        });
        var second = await client.AppendEventAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "TestEvent",
            PayloadJson = "{}",
        });

        Assert.Equal(1, first.StreamVersion);
        Assert.Equal(2, second.StreamVersion);
    }

    [Fact]
    public async Task AppendEvent_ConcurrentWritesToSameStream_AllGetUniqueSequentialVersions()
    {
        await using var host = await DaemonTestHost.CreateAsync();
        await host.StartAsync();
        var streamId = Guid.NewGuid();

        var appends = Enumerable.Range(0, 10).Select(async _ =>
        {
            var client = host.CreateClient();
            var response = await client.AppendEventAsync(new AppendEventRequest
            {
                StreamId = streamId,
                EventType = "TestEvent",
                PayloadJson = "{}",
            });
            return response.StreamVersion;
        });

        var versions = await Task.WhenAll(appends);

        Assert.Equal(Enumerable.Range(1, 10).Select(v => (long)v), versions.OrderBy(v => v));
    }

    [Fact]
    public async Task ReadEvents_ReturnsWhatWasWritten_OrderedByStreamVersion_ServedFromLocalStoreOnly()
    {
        await using var host = await DaemonTestHost.CreateAsync();
        await host.StartAsync();
        var client = host.CreateClient();
        var streamId = Guid.NewGuid();

        await client.AppendEventAsync(new AppendEventRequest { StreamId = streamId, EventType = "First", PayloadJson = "{}" });
        await client.AppendEventAsync(new AppendEventRequest { StreamId = streamId, EventType = "Second", PayloadJson = "{}" });
        await client.AppendEventAsync(new AppendEventRequest { StreamId = streamId, EventType = "Third", PayloadJson = "{}" });

        var result = await client.ReadEventsAsync(new ReadEventsRequest { StreamId = streamId });

        Assert.Equal(3, result.Events.Count);
        Assert.Equal(["First", "Second", "Third"], result.Events.Select(e => e.EventType));
        Assert.Equal([1L, 2L, 3L], result.Events.Select(e => e.StreamVersion));
    }

    [Fact]
    public async Task ReadEvents_ForUnknownStream_ReturnsEmpty()
    {
        await using var host = await DaemonTestHost.CreateAsync();
        await host.StartAsync();
        var client = host.CreateClient();

        var result = await client.ReadEventsAsync(new ReadEventsRequest { StreamId = Guid.NewGuid() });

        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task WrittenEvent_SurvivesAFreshDbContext_SimulatingADaemonRestart()
    {
        await using var host = await DaemonTestHost.CreateAsync();
        await host.StartAsync();
        var client = host.CreateClient();
        var streamId = Guid.NewGuid();

        var appended = await client.AppendEventAsync(new AppendEventRequest
        {
            StreamId = streamId,
            EventType = "TestEvent",
            PayloadJson = """{"value":42}""",
        });

        // A brand new DbContext against the same SQLite file — nothing
        // shared with the running host's provider — simulating what a
        // restarted daemon process would see on next startup.
        await using var freshDb = host.OpenFreshDbContext();
        var stored = await freshDb.Events.FindAsync(appended.GlobalEventId);

        Assert.NotNull(stored);
        Assert.Equal(streamId, stored.StreamId);
        Assert.Equal("""{"value":42}""", stored.PayloadJson);
    }
}
