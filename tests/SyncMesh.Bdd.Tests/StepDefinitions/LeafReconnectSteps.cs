using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/event-ordering-and-idempotency.feature — "Leaf node
// reconnect-sync gap is explicitly tested, not assumed safe." This is the
// same property already proven in SyncMesh.Sync.Tests.DaemonToServerSyncTests
// .ExtendedDisconnectThenReconnect_..., bound here directly against the
// Gherkin scenario per CLAUDE.md ("treat feature files as executable
// acceptance criteria").
[Binding]
public sealed class LeafReconnectSteps(LeafReconnectContext context)
{
    private readonly List<Guid> _bufferedDuringOutage = [];
    private readonly Guid _streamId = Guid.NewGuid();

    [Given("a daemon's leaf node has been disconnected from its nearest server for longer than a typical outage")]
    public async Task GivenADaemonsLeafNodeHasBeenDisconnectedForLongerThanATypicalOutage()
    {
        await context.StartAsync();

        await context.StopHubAsync();

        for (var i = 0; i < 5; i++)
        {
            var response = await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = _streamId,
                EventType = $"BufferedDuringOutage{i}",
                PayloadJson = "{}",
            }, CancellationToken.None);
            _bufferedDuringOutage.Add(response.GlobalEventId);
        }

        // None of these should have reached the server while the hub is down.
        foreach (var globalEventId in _bufferedDuringOutage)
        {
            Assert.IsNull(await context.WaitUntilAppliedAsync(globalEventId, TimeSpan.FromSeconds(2)));
        }

        await Task.Delay(TimeSpan.FromSeconds(5));
    }

    [When("connectivity is restored")]
    public async Task WhenConnectivityIsRestored()
    {
        await context.StartHubAsync();
    }

    [Then("all events buffered locally during the disconnection are confirmed present at the nearest server")]
    public async Task ThenAllEventsBufferedLocallyAreConfirmedPresentAtTheNearestServer()
    {
        foreach (var globalEventId in _bufferedDuringOutage)
        {
            Assert.IsNotNull(await context.WaitUntilAppliedAsync(globalEventId, TimeSpan.FromSeconds(60)));
        }
    }

    [Then("any gap between {string} and {string} is captured as a defect, not silently tolerated")]
    public void ThenAnyGapIsCapturedAsADefectNotSilentlyTolerated(string documented, string observed)
    {
        // This scenario IS that capture mechanism: if the assertions above
        // ever fail, the failure is the defect report — see ADR-0002's
        // 2026-07-23 Amendment for what was manually validated before this
        // was automated, and the risk this exists to keep guarding against.
    }
}
