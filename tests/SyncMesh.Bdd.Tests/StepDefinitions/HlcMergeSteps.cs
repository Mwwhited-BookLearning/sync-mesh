using SyncMesh.Contracts;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

public sealed class HlcMergeContext
{
    public HlcGenerator Generator { get; } = new();
    public HybridLogicalClock LocalBeforeMerge { get; set; }
    public HybridLogicalClock RemoteReceived { get; set; }
    public HybridLogicalClock Merged { get; set; }
}

// docs/bdd/features/event-ordering-and-idempotency.feature —
// "Clock merge preserves causal ordering after receiving a remote event".
// Pure HlcGenerator behavior, no network needed — this is the "HLC
// generation/merge... in isolation" exit criterion for Phase 1.
[Binding]
public sealed class HlcMergeSteps(HlcMergeContext context)
{
    [Given("a site's local HLC counter is at a known state")]
    public void GivenASitesLocalHlcCounterIsAtAKnownState()
    {
        context.LocalBeforeMerge = context.Generator.Next();
    }

    [When("the site receives an event from another site with a later physical time")]
    public void WhenTheSiteReceivesAnEventFromAnotherSiteWithALaterPhysicalTime()
    {
        context.RemoteReceived = new HybridLogicalClock(
            context.LocalBeforeMerge.PhysicalTicks + TimeSpan.FromMinutes(5).Ticks,
            LogicalCounter: 2);

        context.Merged = context.Generator.Merge(context.RemoteReceived);
    }

    [Then("the site's local HLC is merged forward to reflect the later time")]
    public void ThenTheSitesLocalHlcIsMergedForwardToReflectTheLaterTime()
    {
        Assert.IsTrue(context.Merged > context.RemoteReceived);
        Assert.AreEqual(context.RemoteReceived.PhysicalTicks, context.Merged.PhysicalTicks);
    }

    [Then("subsequent locally generated events have HLC values greater than the merged value")]
    public void ThenSubsequentLocallyGeneratedEventsHaveHlcValuesGreaterThanTheMergedValue()
    {
        var subsequent = context.Generator.Next();
        Assert.IsTrue(subsequent > context.Merged);
    }
}
