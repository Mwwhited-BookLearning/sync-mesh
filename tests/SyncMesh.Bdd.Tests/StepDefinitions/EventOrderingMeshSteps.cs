using SyncMesh.Contracts;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/event-ordering-and-idempotency.feature — duplicate
// delivery and reconciliation-after-partition scenarios.
[Binding]
public sealed class EventOrderingMeshSteps(EventOrderingMeshContext context)
{
    private ServerNode _serverB = null!;
    private EventEnvelope _repeatedEnvelope = null!;

    [Given("an event with GlobalEventId {string} has already been applied at Server B")]
    public async Task GivenAnEventHasAlreadyBeenAppliedAtServerB(string _)
    {
        _serverB = await context.CreateServerAsync("server-b");
        _repeatedEnvelope = context.BuildEnvelope("server-b", Guid.NewGuid(), 1, "DuplicateProbe");
        await context.ApplyAsync(_serverB, _repeatedEnvelope);
        Assert.IsTrue(context.LastApplyReplyWasOk);
    }

    [When("Server B receives the same event again \\(at-least-once redelivery\\)")]
    public async Task WhenServerBReceivesTheSameEventAgain()
    {
        await context.ApplyAsync(_serverB, _repeatedEnvelope);
    }

    [Then("Server B does not insert a duplicate record")]
    public async Task ThenServerBDoesNotInsertADuplicateRecord()
    {
        Assert.IsTrue(context.LastApplyReplyWasOk, "Re-delivery should be an idempotent no-op, not an error.");
        Assert.AreEqual(1, await context.GetEventCountAsync(_serverB));
    }

    [Then("Server B's event store state is unchanged by the redelivery")]
    public async Task ThenServerBsEventStoreStateIsUnchangedByTheRedelivery()
    {
        var stored = await context.FindEventAsync(_serverB, _repeatedEnvelope.GlobalEventId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(_repeatedEnvelope.EventType, stored!.EventType);
    }

    private ServerNode _nodeA = null!;
    private ServerNode _nodeB = null!;
    private Guid _duringOutageEvent1 = default;
    private Guid _duringOutageEvent2 = default;
    private Guid _afterReconnectEvent = default;

    [Given("a site has been disconnected from the mesh for an extended period")]
    public async Task GivenASiteHasBeenDisconnectedFromTheMeshForAnExtendedPeriod()
    {
        var (urlA, containerA) = await context.PrepareContainerAsync("ordering-node-a");
        var (urlB, containerB) = await context.PrepareContainerAsync("ordering-node-b");
        _nodeA = await context.StartServerAsync("ordering-node-a", urlA, containerA, ("ordering-node-b", urlB));
        _nodeB = await context.StartServerAsync("ordering-node-b", urlB, containerB, ("ordering-node-a", urlA));

        await _nodeB.StopContainerAsync();
    }

    [Given("both the disconnected site and the connected mesh have continued producing events")]
    public async Task GivenBothSidesHaveContinuedProducingEvents()
    {
        // Deliberately early HLC physical ticks — these events "logically"
        // happened before anything applied directly at B below, even
        // though (being stuck behind B's outage) they won't actually
        // arrive at B until well after B's own event is recorded.
        var streamId = Guid.NewGuid();
        var envelope1 = context.BuildEnvelope("ordering-node-a", streamId, 1, "DuringOutage1", new HybridLogicalClock(1000, 0));
        var envelope2 = context.BuildEnvelope("ordering-node-a", streamId, 2, "DuringOutage2", new HybridLogicalClock(1001, 0));
        await context.ApplyAsync(_nodeA, envelope1);
        _duringOutageEvent1 = envelope1.GlobalEventId;
        await context.ApplyAsync(_nodeA, envelope2);
        _duringOutageEvent2 = envelope2.GlobalEventId;
    }

    [When("the disconnected site reconnects and exchanges event logs")]
    public async Task WhenTheDisconnectedSiteReconnectsAndExchangesEventLogs()
    {
        await _nodeB.StartContainerAsync();

        // "The connected mesh" produces its own event right after
        // reconnect — a much later HLC physical tick than the two stuck
        // behind the outage above, and it lands at B (self-request, no
        // forwarding round trip) before A's backlog necessarily arrives.
        var afterReconnect = context.BuildEnvelope("ordering-node-b", Guid.NewGuid(), 1, "AfterReconnect", new HybridLogicalClock(99_999_999_999, 0));
        await context.ApplyAsync(_nodeB, afterReconnect);
        _afterReconnectEvent = afterReconnect.GlobalEventId;
    }

    [Then("all events from both sides are present in the reconciled history")]
    public async Task ThenAllEventsFromBothSidesArePresentInTheReconciledHistory()
    {
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_nodeB, _duringOutageEvent1, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_nodeB, _duringOutageEvent2, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_nodeA, _afterReconnectEvent, context, TimeSpan.FromSeconds(30)));
    }

    [Then("the reconciled history's replay order is consistent with each event's HLC value")]
    public async Task ThenTheReconciledHistorysReplayOrderIsConsistentWithEachEventsHlcValue()
    {
        var replay = await context.ReplayInHlcOrderAsync(_nodeB);
        var ids = replay.Select(e => e.GlobalEventId).ToList();

        // Arrival order at B was [AfterReconnect, DuringOutage1, DuringOutage2]
        // (B's own self-applied event landed before A's stuck backlog
        // arrived). HLC order must be the reverse of that for the two
        // outage events relative to the reconnect event.
        var duringOutage1Index = ids.IndexOf(_duringOutageEvent1);
        var duringOutage2Index = ids.IndexOf(_duringOutageEvent2);
        var afterReconnectIndex = ids.IndexOf(_afterReconnectEvent);

        Assert.IsTrue(duringOutage1Index < duringOutage2Index, "DuringOutage1 (earlier HLC) must replay before DuringOutage2.");
        Assert.IsTrue(duringOutage2Index < afterReconnectIndex, "Both outage-window events (earlier HLC) must replay before AfterReconnect, despite arriving at B after it.");
    }
}
