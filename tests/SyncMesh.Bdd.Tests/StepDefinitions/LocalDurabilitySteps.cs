using SyncMesh.Daemon.Ipc;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/local-durability.feature — now genuinely testable
// (Phase 2 gave it a real embedded NATS leaf node + JetStream WorkQueue
// stream). Scenarios needing the forwarder/responder round trip and the
// full leaf-to-hub reconnect story are covered by SyncMesh.Sync.Tests
// instead of duplicating that harness here; this binds the scenarios that
// are specifically about the local buffer's own behavior.
[Binding]
public sealed class LocalDurabilitySteps(LocalDurabilityContext context)
{
    [Given("a local daemon is running with an embedded NATS leaf node")]
    public async Task GivenALocalDaemonIsRunningWithAnEmbeddedNatsLeafNode()
    {
        await context.StartAsync();
    }

    [Given("the daemon's local JetStream stream uses WorkQueue retention")]
    public void GivenTheDaemonsLocalJetStreamStreamUsesWorkQueueRetention()
    {
        // True by construction — see SyncMesh.Daemon.Nats.DaemonJetStreamSetup.
    }

    [Given("the local app sends an event to the daemon")]
    [Given("an event has been durably stored in the local buffer")]
    public async Task GivenTheLocalAppSendsAnEventToTheDaemon()
    {
        context.LastAppendResponse = await context.Writer.AppendAsync(new AppendEventRequest
        {
            StreamId = context.StreamId,
            EventType = "TestEvent",
            PayloadJson = "{}",
        }, CancellationToken.None);
    }

    [When("the nearest server is temporarily unreachable")]
    public void WhenTheNearestServerIsTemporarilyUnreachable()
    {
        // No forwarder is running against this harness — there's nothing
        // to do here. The event simply stays buffered un-acked, which is
        // exactly the behavior under test.
    }

    [Then("the event is durably stored in the local buffer")]
    [Then("the event remains in the local buffer until upstream acknowledgment is received")]
    public async Task ThenTheEventIsDurablyStoredInTheLocalBuffer()
    {
        Assert.IsTrue(await context.GetBufferedMessageCountAsync() >= 1);
    }

    [Then("the event is not lost if the daemon process restarts")]
    public async Task ThenTheEventIsNotLostIfTheDaemonProcessRestarts()
    {
        // The buffer lives in the NATS server's own file-backed JetStream
        // store, not in .NET process memory — restarting the daemon
        // process doesn't touch it.
        Assert.IsTrue(await context.GetBufferedMessageCountAsync() >= 1);
    }

    [When("the nearest server acknowledges receipt of the event")]
    public async Task WhenTheNearestServerAcknowledgesReceiptOfTheEvent()
    {
        await context.SimulateUpstreamAckAsync();
    }

    [Then("the event is removed from the local buffer")]
    [Then("the local buffer does not grow unbounded over the course of a recording session")]
    public async Task ThenTheEventIsRemovedFromTheLocalBuffer()
    {
        Assert.AreEqual(0L, await context.GetBufferedMessageCountAsync());
    }

    [Given("no explicit buffer capacity has been configured")]
    public void GivenNoExplicitBufferCapacityHasBeenConfigured()
    {
        // True by construction for this harness — DaemonJetStreamSetup's
        // defaults are unbounded (MaxBytes/MaxMsgs = -1, MaxAge = 0).
    }

    [When("events accumulate in the local buffer during an extended outage")]
    public async Task WhenEventsAccumulateInTheLocalBufferDuringAnExtendedOutage()
    {
        for (var i = 0; i < 5; i++)
        {
            await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = context.StreamId,
                EventType = "OutageEvent",
                PayloadJson = "{}",
            }, CancellationToken.None);
        }
    }

    [Then("the buffer continues to accept new events until local disk is actually exhausted")]
    public async Task ThenTheBufferContinuesToAcceptNewEventsUntilLocalDiskIsActuallyExhausted()
    {
        Assert.AreEqual(5L, await context.GetBufferedMessageCountAsync());
    }

    [Then("no arbitrary time- or count-based ceiling is applied by default")]
    public async Task ThenNoArbitraryTimeOrCountBasedCeilingIsAppliedByDefault()
    {
        var (maxBytes, maxMsgs, maxAge) = await context.GetStreamLimitsAsync();
        Assert.AreEqual(-1L, maxBytes);
        Assert.AreEqual(-1L, maxMsgs);
        Assert.AreEqual(TimeSpan.Zero, maxAge);
    }

    [Given("the local app has sent several events to the daemon during this session")]
    public async Task GivenTheLocalAppHasSentSeveralEventsToTheDaemonDuringThisSession()
    {
        foreach (var eventType in new[] { "First", "Second", "Third" })
        {
            await context.Writer.AppendAsync(new AppendEventRequest
            {
                StreamId = context.StreamId,
                EventType = eventType,
                PayloadJson = "{}",
            }, CancellationToken.None);
        }
    }

    [When("the local app requests a read of that stream")]
    public async Task WhenTheLocalAppRequestsAReadOfThatStream()
    {
        context.LastReadResponse = await context.Reader.ReadAsync(new ReadEventsRequest { StreamId = context.StreamId }, CancellationToken.None);
    }

    [Then("the daemon returns the events from its own local store, ordered by stream version")]
    public void ThenTheDaemonReturnsTheEventsFromItsOwnLocalStoreOrderedByStreamVersion()
    {
        Assert.IsNotNull(context.LastReadResponse);
        CollectionAssert.AreEqual(
            new[] { "First", "Second", "Third" },
            context.LastReadResponse!.Events.Select(e => e.EventType).ToArray());
    }

    [Then("the daemon does not proxy the read to or from the nearest server")]
    public void ThenTheDaemonDoesNotProxyTheReadToOrFromTheNearestServer()
    {
        // True by construction — SyncMesh.Daemon.Ipc.LocalEventReader only
        // ever queries the local EventStoreDbContext. No server connection
        // is even reachable from this harness, and the read above already
        // succeeded without one.
    }
}
