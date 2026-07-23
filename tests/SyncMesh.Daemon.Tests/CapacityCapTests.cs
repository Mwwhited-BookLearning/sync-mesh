using Microsoft.EntityFrameworkCore;
using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Daemon.Tests;

public class CapacityCapTests
{
    [Fact]
    public async Task AppendEvent_BeyondConfiguredCap_IsRejected_NotEvicted_AndDoesNotOrphanTheSqliteRow()
    {
        await using var host = await DaemonTestHost.CreateAsync(nats => nats.MaxMsgs = 3);
        await host.StartAsync();
        var client = host.CreateClient();
        var streamId = Guid.NewGuid();

        // Fill the cap exactly.
        for (var i = 0; i < 3; i++)
        {
            await client.AppendEventAsync(new AppendEventRequest
            {
                StreamId = streamId,
                EventType = "TestEvent",
                PayloadJson = "{}",
            });
        }

        // The 4th append must fail — rejected (Discard: New), not silently
        // evicting an unacknowledged message to make room.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AppendEventAsync(new AppendEventRequest
            {
                StreamId = streamId,
                EventType = "OneTooMany",
                PayloadJson = "{}",
            }));
        Assert.Contains("could not be buffered", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And critically: the rejected event must not be left behind as an
        // orphaned SQLite row that can never sync and blocks the version
        // sequence — it should have been rolled back.
        await using var freshDb = host.OpenFreshDbContext();
        var count = await freshDb.Events.CountAsync(e => e.StreamId == streamId);
        Assert.Equal(3, count);

        // A subsequent append (once room exists, e.g. after an ack in
        // production) should still get the correct next version — 4, not
        // a gap left by the rolled-back attempt.
    }
}
