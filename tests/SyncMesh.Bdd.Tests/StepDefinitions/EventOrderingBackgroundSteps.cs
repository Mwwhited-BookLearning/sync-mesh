namespace SyncMesh.Bdd.Tests.StepDefinitions;

// Scene-setting Background shared by every scenario in
// event-ordering-and-idempotency.feature. Both preconditions are
// structural guarantees of the data model (SyncMesh.Contracts.EventEnvelope
// / SyncMesh.EventStore.EventRecord), not something to simulate here —
// each scenario constructs whatever multi-site data it needs itself.
[Binding]
public sealed class EventOrderingBackgroundSteps
{
    [Given("multiple sites are producing events independently")]
    public void GivenMultipleSitesAreProducingEventsIndependently()
    {
    }

    [Given("each event carries a GlobalEventId, OriginSiteId, and HybridLogicalClock value")]
    public void GivenEachEventCarriesAGlobalEventIdOriginSiteIdAndHybridLogicalClockValue()
    {
    }
}
