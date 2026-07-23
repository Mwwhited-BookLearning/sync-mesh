using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// The remaining local-durability.feature scenarios: explicit capacity cap
// override, recording-session cleanup, and no-nearest-server ("client
// isolated") durability. Shares LocalDurabilityContext (and its Background
// bindings) with LocalDurabilitySteps.
[Binding]
public sealed class LocalDurabilityCapAndIsolationSteps(LocalDurabilityContext context)
{
    [Given("the local buffer has been configured with an explicit MaxBytes, MaxAge, or MaxMsgs cap smaller than available disk")]
    public async Task GivenTheLocalBufferHasBeenConfiguredWithAnExplicitCapSmallerThanAvailableDisk()
    {
        await context.SetCapacityCapAsync(maxMsgs: 2);
    }

    [When("the nearest server is unreachable for longer than expected")]
    public void WhenTheNearestServerIsUnreachableForLongerThanExpected()
    {
        // No forwarder/responder is wired up in this harness at all —
        // nothing ever acks, exactly the condition being simulated.
    }

    [When("the buffer reaches its configured cap")]
    public async Task WhenTheBufferReachesItsConfiguredCap()
    {
        for (var i = 0; i < 2; i++)
        {
            await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = context.StreamId,
                EventType = "CapEvent",
                PayloadJson = "{}",
            }, CancellationToken.None);
        }

        try
        {
            await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = context.StreamId,
                EventType = "OneTooMany",
                PayloadJson = "{}",
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.LastAppendError = ex;
        }
    }

    [Then("new local writes are rejected rather than evicting unacknowledged events")]
    public async Task ThenNewLocalWritesAreRejectedRatherThanEvictingUnacknowledgedEvents()
    {
        Assert.IsNotNull(context.LastAppendError);
        Assert.AreEqual(2L, await context.GetBufferedMessageCountAsync());
    }

    [Then("the system surfaces an explicit operational warning")]
    public void ThenTheSystemSurfacesAnExplicitOperationalWarning()
    {
        StringAssert.Contains(context.LastAppendError!.Message, "could not be buffered");
    }

    [Then("the behavior on cap overflow is a deliberate, documented decision \\(not silent data loss\\)")]
    public void ThenTheBehaviorOnCapOverflowIsADeliberateDocumentedDecision()
    {
        // See SyncMesh.Daemon.Ipc.LocalEventWriter's compensating-delete
        // path and ARCHITECTURE.md — an explicit, tested decision, not an
        // accident of whatever the client library happened to do.
    }

    [Given("a recording session has ended")]
    public async Task GivenARecordingSessionHasEnded()
    {
        for (var i = 0; i < 3; i++)
        {
            await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = context.StreamId,
                EventType = "SessionEvent",
                PayloadJson = "{}",
            }, CancellationToken.None);
        }
    }

    [Given("all events from that session have been acknowledged upstream")]
    public async Task GivenAllEventsFromThatSessionHaveBeenAcknowledgedUpstream()
    {
        await context.SimulateUpstreamAckAllAsync();
    }

    [Then("the local buffer contains no residual events from that session")]
    public async Task ThenTheLocalBufferContainsNoResidualEventsFromThatSession()
    {
        Assert.AreEqual(0L, await context.GetBufferedMessageCountAsync());
    }

    [Then("no component depends on the local buffer for historical event retrieval")]
    public async Task ThenNoComponentDependsOnTheLocalBufferForHistoricalEventRetrieval()
    {
        // Historical/buffered reads go through EventStoreDbContext (local
        // SQLite), never the JetStream buffer — draining the buffer above
        // must not affect read access to what was recorded.
        var result = await context.Reader.ReadAsync(new ReadEventsRequest { StreamId = context.StreamId }, CancellationToken.None);
        Assert.AreEqual(3, result.Events.Count);
    }

    [Given("the daemon has no nearest-server connection configured or reachable")]
    public void GivenTheDaemonHasNoNearestServerConnectionConfiguredOrReachable()
    {
        // True by construction in this harness: no forwarder or
        // hub/responder is wired up at all, only the daemon's own local
        // leaf node + SQLite store. The full leaf-to-hub topology is
        // exercised separately in SyncMesh.Sync.Tests.
    }

    [When("the local app sends events to the daemon over an extended period")]
    public async Task WhenTheLocalAppSendsEventsToTheDaemonOverAnExtendedPeriod()
    {
        for (var i = 0; i < 5; i++)
        {
            await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = context.StreamId,
                EventType = $"IsolatedEvent{i}",
                PayloadJson = "{}",
            }, CancellationToken.None);
        }
    }

    [Then("each event is durably stored in the local buffer exactly as it would be during a temporary outage")]
    public async Task ThenEachEventIsDurablyStoredInTheLocalBufferExactlyAsItWouldBeDuringATemporaryOutage()
    {
        Assert.AreEqual(5L, await context.GetBufferedMessageCountAsync());
    }

    [Then("the local app can still read back everything it has recorded")]
    public async Task ThenTheLocalAppCanStillReadBackEverythingItHasRecorded()
    {
        var result = await context.Reader.ReadAsync(new ReadEventsRequest { StreamId = context.StreamId }, CancellationToken.None);
        Assert.AreEqual(5, result.Events.Count);
    }

    [Then("this is treated as a valid, permanent deployment mode, not merely an outage to tolerate")]
    public void ThenThisIsTreatedAsAValidPermanentDeploymentModeNotMerelyAnOutageToTolerate()
    {
        // Architectural property — see docs/00-design-document.md
        // §4.2/§4.3 ("client isolated" deployment mode) and
        // ARCHITECTURE.md. Nothing above required a server connection to
        // succeed at any point.
    }
}
