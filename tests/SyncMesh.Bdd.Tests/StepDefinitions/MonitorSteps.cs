using SyncMesh.Contracts;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/remote-monitoring-tunnel.feature — the Phase 4
// passive-monitoring scenario. The other scenarios in this file (direct
// tunnel, relay fallback, TLS/service-credential auth, cross-failure
// isolation) are Phase 5/6 scope and remain correctly pending — no tunnel
// mechanism exists yet to bind them against.
[Binding]
public sealed class MonitorSteps(MonitorContext context)
{
    private DaemonStatus? _received;

    [Given("a recording instance is actively recording")]
    public void GivenARecordingInstanceIsActivelyRecording()
    {
        // Scene-setting — this scenario only exercises the monitor-
        // telemetry path itself, not the recording/write path.
    }

    [Given("a remote user has valid credentials to monitor or tunnel to it")]
    public void GivenARemoteUserHasValidCredentialsToMonitorOrTunnelToIt()
    {
        // True by construction for this phase — TLS + registered service
        // credentials is a decided baseline (ADR-0002/ADR-0004) but
        // explicitly deferred to Phase 6 for POC. See ARCHITECTURE.md and
        // WORKPLAN.md's "Deferred to Phase 6" notes.
    }

    [Given("the interactive tunnel path is currently blocked and no relay session is active")]
    public void GivenTheInteractiveTunnelPathIsCurrentlyBlockedAndNoRelaySessionIsActive()
    {
        // True by construction — no tunnel/relay mechanism exists yet
        // (Phase 5), so this is trivially satisfied. What this scenario
        // actually proves is that monitoring doesn't need one to exist.
    }

    [When("the daemon publishes telemetry to its monitor subject")]
    public async Task WhenTheDaemonPublishesTelemetryToItsMonitorSubject()
    {
        await context.StartAsync();
        _received = await context.WaitForStatusAsync(TimeSpan.FromSeconds(15));
    }

    [Then("the remote user still receives monitoring data via the existing event-mesh routing")]
    public void ThenTheRemoteUserStillReceivesMonitoringDataViaTheExistingEventMeshRouting()
    {
        Assert.IsNotNull(_received, "Expected a DaemonStatus message to cross the leaf boundary to a hub-side subscriber.");
        Assert.AreEqual(context.SiteId, _received!.SiteId);
        Assert.AreEqual(context.InstanceId, _received.InstanceId);
        Assert.IsTrue(_received.ConnectedToNearestServer, "The daemon's leaf connection should be up during this scenario.");
    }
}
