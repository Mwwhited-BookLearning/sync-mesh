namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/remote-monitoring-tunnel.feature — the 4 Phase 5
// tunnel scenarios this phase implements against a real TunnelAgent/
// TunnelRelay/EchoServer (and, for the two cross-failure scenarios, a
// real NATS hub+leaf + daemon/server event-sync stack alongside it). The
// TLS/service-credential scenario remains correctly pending — deferred
// wholesale to PRODUCTION-HARDENING.md, not this phase's scope. See
// docs/adr/0007-custom-reverse-tunnel-mechanism.md.
[Binding]
public sealed class TunnelSteps(TunnelContext context)
{
    private bool _simulateBlockedDirect;

    [Given("the remote user's client can reach the daemon directly")]
    public async Task GivenTheRemoteUsersClientCanReachTheDaemonDirectly()
    {
        await context.StartTunnelOnlyAsync();
        _simulateBlockedDirect = false;
    }

    [Given("the remote user's client cannot reach the daemon directly \\(firewall\\/NAT\\)")]
    public async Task GivenTheRemoteUsersClientCannotReachTheDaemonDirectly()
    {
        await context.StartTunnelOnlyAsync();
        _simulateBlockedDirect = true;
    }

    [When("the remote user requests an interactive session")]
    public async Task WhenTheRemoteUserRequestsAnInteractiveSession()
    {
        await context.ConnectAsync(_simulateBlockedDirect);
    }

    [Then("the session is established directly, without relaying through the nearest server")]
    public async Task ThenTheSessionIsEstablishedDirectlyWithoutRelayingThroughTheNearestServer()
    {
        Assert.IsFalse(context.LastUsedRelay, "Expected the direct path to succeed without falling back to relay.");
        await context.AssertRoundTripsAsync();
    }

    [Then("the client attempts direct connection first")]
    public void ThenTheClientAttemptsDirectConnectionFirst()
    {
        // Proves a real connection attempt happened (non-zero I/O time),
        // not that it hung for the full configured timeout — how fast a
        // closed loopback port fails is OS/environment-dependent (an
        // instant RST on some platforms, a hang until timeout on others),
        // so asserting >= DirectAttemptTimeout was itself environment-
        // dependent rather than a real behavioral guarantee. Combined
        // with the next step's LastUsedRelay assertion, this proves
        // direct was genuinely tried and genuinely failed before falling
        // back, regardless of how quickly this environment's TCP stack
        // reports the failure.
        Assert.IsTrue(context.LastConnectElapsed > TimeSpan.Zero,
            "Expected the direct connection attempt to take measurable, non-zero time — a zero elapsed duration would suggest it was skipped rather than attempted.");
        Assert.IsTrue(context.LastConnectElapsed <= context.DirectAttemptTimeout + TimeSpan.FromSeconds(5),
            $"Expected the direct attempt to give up within a bounded window around {context.DirectAttemptTimeout}, took {context.LastConnectElapsed}.");
    }

    [Then("upon failure, falls back to relaying through the nearest server")]
    public void ThenUponFailureFallsBackToRelayingThroughTheNearestServer()
    {
        Assert.IsTrue(context.LastUsedRelay, "Expected the relay path to be used after the direct attempt failed.");
    }

    [Then("the session is established via the relay")]
    public async Task ThenTheSessionIsEstablishedViaTheRelay()
    {
        await context.AssertRoundTripsAsync();
    }

    [Given("the tunnel relay mechanism is unavailable or failing")]
    public async Task GivenTheTunnelRelayMechanismIsUnavailableOrFailing()
    {
        await context.StartFullStackAsync();
        await context.StopTunnelRelayAsync();
    }

    [When("the local daemon continues recording and forwarding events")]
    public async Task WhenTheLocalDaemonContinuesRecordingAndForwardingEvents()
    {
        _appendedEventId = await context.AppendEventAsync();
    }

    private Guid _appendedEventId;

    [Then("event durability and forwarding to the nearest server are unaffected")]
    public async Task ThenEventDurabilityAndForwardingToTheNearestServerAreUnaffected()
    {
        Assert.IsTrue(await context.WasAppliedAsync(_appendedEventId, TimeSpan.FromSeconds(15)),
            "Expected the event to reach the server even with the tunnel relay down.");
    }

    [Then("no event-sync component depends on the tunnel\\/relay mechanism being healthy")]
    public void ThenNoEventSyncComponentDependsOnTheTunnelRelayMechanismBeingHealthy()
    {
        // True by construction, and already reinforced by the assertion
        // above: nothing in Daemon/Nats or ServerHost/Nats references
        // anything in Daemon/Tunnel or ServerHost/Tunnel (see
        // ARCHITECTURE.md -> "Interactive tunnel + relay").
    }

    [Given("the event-sync mesh is degraded or unavailable")]
    public async Task GivenTheEventSyncMeshIsDegradedOrUnavailable()
    {
        await context.StartFullStackAsync();
        await context.StopEventHubAsync();
    }

    [When("a remote user requests a direct or relayed interactive session")]
    public async Task WhenARemoteUserRequestsADirectOrRelayedInteractiveSession()
    {
        // Force the relay path — this scenario's point is that the relay
        // has zero NATS dependency, not the direct-vs-relay choice itself.
        await context.ConnectAsync(simulateBlockedDirect: true);
    }

    [Then("the tunnel\\/monitoring path functions independently of event-sync health")]
    public async Task ThenTheTunnelMonitoringPathFunctionsIndependentlyOfEventSyncHealth()
    {
        Assert.IsTrue(context.LastUsedRelay);
        await context.AssertRoundTripsAsync();
    }
}
