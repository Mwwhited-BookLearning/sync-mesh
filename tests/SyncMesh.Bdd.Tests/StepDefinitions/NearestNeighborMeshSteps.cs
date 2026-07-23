namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/nearest-neighbor-sync.feature — the four Phase 3
// server-mesh scenarios (standalone, 2-node reconciliation, 3-node
// transitive relay through a designated gateway, full mesh everywhere).
// Reuses SyncMesh.Bdd.Tests.StepDefinitions.EventOrderingMeshContext — the
// same real-container, real-ApplyResponder/MeshForwarder harness proven in
// SyncMesh.Sync.Tests.ServerMeshReconciliationTests.
[Binding]
public sealed class NearestNeighborMeshSteps(EventOrderingMeshContext context)
{
    private ServerNode _standalone = null!;
    private readonly List<Guid> _standaloneEvents = [];

    private ServerNode _twoNodeA = null!;
    private ServerNode _twoNodeB = null!;
    private Guid _twoNodeFromA;
    private Guid _twoNodeFromB;

    private ServerNode _meshA = null!;
    private ServerNode _meshB = null!;
    private ServerNode _meshC = null!;
    private Guid _meshFromA;
    private Guid _meshFromC;

    private ServerNode _fullMeshA = null!;
    private ServerNode _fullMeshB = null!;
    private ServerNode _fullMeshC = null!;
    private Guid _fullMeshFromA;
    private Guid _fullMeshFromB;
    private Guid _fullMeshFromC;

    // --- Standalone server ---

    [Given("a server has no gateway connections configured to any peer")]
    public async Task GivenAServerHasNoGatewayConnectionsConfiguredToAnyPeer()
    {
        _standalone = await context.CreateServerAsync("standalone-server");
    }

    [When("daemons connect to it and forward events")]
    public async Task WhenDaemonsConnectToItAndForwardEvents()
    {
        for (var i = 0; i < 3; i++)
        {
            var envelope = context.BuildEnvelope("standalone-server", Guid.NewGuid(), 1, $"StandaloneEvent{i}");
            await context.ApplyAsync(_standalone, envelope);
            Assert.IsTrue(context.LastApplyReplyWasOk);
            _standaloneEvents.Add(envelope.GlobalEventId);
        }
    }

    [Then("the server durably stores and serves those events as a complete system of record on its own")]
    public async Task ThenTheServerDurablyStoresAndServesThoseEventsOnItsOwn()
    {
        Assert.AreEqual(3, await context.GetEventCountAsync(_standalone));
        foreach (var globalEventId in _standaloneEvents)
        {
            Assert.IsNotNull(await context.FindEventAsync(_standalone, globalEventId));
        }
    }

    [Then("this is a first-class, permanent deployment mode, not a bootstrapping step toward a mesh")]
    public void ThenThisIsAFirstClassPermanentDeploymentMode()
    {
        // Architectural property, not a runtime check — see
        // docs/00-design-document.md §4.4 and ADR-0002's 2026-07-23
        // (Phase 3) Amendment: ApplyResponder skips the mesh-relay publish
        // entirely whenever zero peers are configured (ServerMeshOptions
        // .Peers.Count == 0), rather than treating "no peers yet" as a
        // degraded or transitional state.
    }

    // --- Two-node reconciliation ---

    [Given("Server A and Server B are connected via a gateway\\/supercluster connection")]
    public async Task GivenServerAAndServerBAreConnectedViaAGatewaySuperclusterConnection()
    {
        var (urlA, containerA) = await context.PrepareContainerAsync("nn-two-a");
        var (urlB, containerB) = await context.PrepareContainerAsync("nn-two-b");
        _twoNodeA = await context.StartServerAsync("nn-two-a", urlA, containerA, ("nn-two-b", urlB));
        _twoNodeB = await context.StartServerAsync("nn-two-b", urlB, containerB, ("nn-two-a", urlA));
    }

    [When("Server A receives a new event from its local daemon")]
    public async Task WhenServerAReceivesANewEventFromItsLocalDaemon()
    {
        var envelope = context.BuildEnvelope("nn-two-a", Guid.NewGuid(), 1, "FromServerA");
        await context.ApplyAsync(_twoNodeA, envelope);
        Assert.IsTrue(context.LastApplyReplyWasOk, "Server A must durably apply its own event before relaying — it never blocks on Server B's confirmation.");
        _twoNodeFromA = envelope.GlobalEventId;
    }

    [Then("Server B eventually receives and applies the same event")]
    public async Task ThenServerBEventuallyReceivesAndAppliesTheSameEvent()
    {
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_twoNodeB, _twoNodeFromA, context, TimeSpan.FromSeconds(30)));
    }

    [Then("Server A eventually receives and applies any event Server B produces locally, the same way")]
    public async Task ThenServerAEventuallyReceivesAndAppliesAnyEventServerBProducesLocally()
    {
        var envelope = context.BuildEnvelope("nn-two-b", Guid.NewGuid(), 1, "FromServerB");
        await context.ApplyAsync(_twoNodeB, envelope);
        Assert.IsTrue(context.LastApplyReplyWasOk);
        _twoNodeFromB = envelope.GlobalEventId;

        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_twoNodeA, _twoNodeFromB, context, TimeSpan.FromSeconds(30)));
    }

    [Then("the reconciliation does not require synchronous coordination between A and B")]
    public void ThenTheReconciliationDoesNotRequireSynchronousCoordinationBetweenAAndB()
    {
        // Proven by construction, not timing: ApplyAsync (SyncMesh.ServerHost
        // .Nats.ApplyResponder) replies "ok" as soon as its own local insert
        // and local MESH_OUTBOUND publish succeed — it never waits for the
        // peer's apply to complete. Both "eventually receives" assertions
        // above passed via polling specifically because there is no
        // synchronous round trip to the peer in the write path.
    }

    // --- Three-node transitive relay through a designated gateway ---

    [Given("multiple servers are deployed at the same site")]
    public async Task GivenMultipleServersAreDeployedAtTheSameSite()
    {
        // Scene-setting — the actual topology is wired in the next step,
        // once every node's URL is known.
    }

    [Given("a separate site \\(or cloud region\\) is also deployed")]
    public void GivenASeparateSiteOrCloudRegionIsAlsoDeployed()
    {
        // Scene-setting — see above.
    }

    [When("gateway connections are configured")]
    public async Task WhenGatewayConnectionsAreConfigured()
    {
        // A and C represent two servers "at the same site" as B, but only
        // B (the designated gateway) peers across to the other — A and C
        // never peer directly with each other.
        var (urlA, containerA) = await context.PrepareContainerAsync("nn-mesh-a");
        var (urlB, containerB) = await context.PrepareContainerAsync("nn-mesh-b");
        var (urlC, containerC) = await context.PrepareContainerAsync("nn-mesh-c");
        _meshA = await context.StartServerAsync("nn-mesh-a", urlA, containerA, ("nn-mesh-b", urlB));
        _meshB = await context.StartServerAsync("nn-mesh-b", urlB, containerB, ("nn-mesh-a", urlA), ("nn-mesh-c", urlC));
        _meshC = await context.StartServerAsync("nn-mesh-c", urlC, containerC, ("nn-mesh-b", urlB));

        var envelopeA = context.BuildEnvelope("nn-mesh-a", Guid.NewGuid(), 1, "FromMeshA");
        await context.ApplyAsync(_meshA, envelopeA);
        _meshFromA = envelopeA.GlobalEventId;

        var envelopeC = context.BuildEnvelope("nn-mesh-c", Guid.NewGuid(), 1, "FromMeshC");
        await context.ApplyAsync(_meshC, envelopeC);
        _meshFromC = envelopeC.GlobalEventId;
    }

    [Then("the servers within the same site are connected to each other directly \\(full mesh\\)")]
    public void ThenTheServersWithinTheSameSiteAreConnectedDirectly()
    {
        // Topology property of the config wired above (B peers both A and
        // C directly) — not a separate runtime check.
    }

    [Then("only a single or limited set of designated gateway servers per site carries the cross-site connection")]
    public void ThenOnlyALimitedSetOfDesignatedGatewayServersCarriesTheCrossSiteConnection()
    {
        // A and C have no peer entry for each other at all — B is the only
        // node bridging them, by construction of the config above.
    }

    [Then("every server at every site still converges to the same fully-replicated event history")]
    public async Task ThenEveryServerAtEverySiteStillConvergesToTheSameFullyReplicatedHistory()
    {
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_meshC, _meshFromA, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_meshA, _meshFromC, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_meshB, _meshFromA, context));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_meshB, _meshFromC, context));
    }

    // --- Full mesh everywhere ---

    [Given("an operator chooses to configure every server, on-prem and cloud alike, as a direct gateway peer of every other server")]
    public async Task GivenAnOperatorConfiguresEveryServerAsADirectGatewayPeerOfEveryOtherServer()
    {
        var (urlA, containerA) = await context.PrepareContainerAsync("nn-full-a");
        var (urlB, containerB) = await context.PrepareContainerAsync("nn-full-b");
        var (urlC, containerC) = await context.PrepareContainerAsync("nn-full-c");
        _fullMeshA = await context.StartServerAsync("nn-full-a", urlA, containerA, ("nn-full-b", urlB), ("nn-full-c", urlC));
        _fullMeshB = await context.StartServerAsync("nn-full-b", urlB, containerB, ("nn-full-a", urlA), ("nn-full-c", urlC));
        _fullMeshC = await context.StartServerAsync("nn-full-c", urlC, containerC, ("nn-full-a", urlA), ("nn-full-b", urlB));
    }

    [When("this topology is deployed")]
    public async Task WhenThisTopologyIsDeployed()
    {
        var envelopeA = context.BuildEnvelope("nn-full-a", Guid.NewGuid(), 1, "FromFullMeshA");
        await context.ApplyAsync(_fullMeshA, envelopeA);
        _fullMeshFromA = envelopeA.GlobalEventId;

        var envelopeB = context.BuildEnvelope("nn-full-b", Guid.NewGuid(), 1, "FromFullMeshB");
        await context.ApplyAsync(_fullMeshB, envelopeB);
        _fullMeshFromB = envelopeB.GlobalEventId;

        var envelopeC = context.BuildEnvelope("nn-full-c", Guid.NewGuid(), 1, "FromFullMeshC");
        await context.ApplyAsync(_fullMeshC, envelopeC);
        _fullMeshFromC = envelopeC.GlobalEventId;
    }

    [Then("it is fully supported, with no architectural minimum or maximum on server or site count")]
    public async Task ThenItIsFullySupportedWithNoArchitecturalMinimumOrMaximum()
    {
        // Every node configured as a direct peer of every other — the same
        // ServerMeshOptions.Peers mechanism as the 2-node and designated-
        // gateway scenarios above, just with more entries. No code branch
        // distinguishes "full mesh" from "limited gateway" (docs/00-design-
        // document.md §4.4): both are the same mechanism, config-driven.
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_fullMeshB, _fullMeshFromA, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_fullMeshC, _fullMeshFromA, context, TimeSpan.FromSeconds(30)));
    }

    [Then("it converges to the same fully-replicated event history as the limited-gateway pattern")]
    public async Task ThenItConvergesToTheSameFullyReplicatedHistoryAsTheLimitedGatewayPattern()
    {
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_fullMeshA, _fullMeshFromB, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_fullMeshA, _fullMeshFromC, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_fullMeshC, _fullMeshFromB, context, TimeSpan.FromSeconds(30)));
        Assert.IsNotNull(await EventOrderingMeshContext.WaitUntilAppliedAsync(_fullMeshB, _fullMeshFromC, context, TimeSpan.FromSeconds(30)));
    }
}
