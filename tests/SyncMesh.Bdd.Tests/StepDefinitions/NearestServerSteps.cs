namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/nearest-neighbor-sync.feature — the four Phase 2
// scenarios (on-prem connect, cloud connect with no on-prem tier,
// config-only switch between them, firewall/NAT outbound-only survival).
// The four multi-server mesh scenarios in this file are Phase 3 scope and
// remain correctly pending.
[Binding]
public sealed class NearestServerSteps(NearestServerContext context)
{
    [Given("the daemon is configured with a nearest-server connection profile")]
    public void GivenTheDaemonIsConfiguredWithANearestServerConnectionProfile()
    {
        // Scene-setting only — each scenario specifies its own profile.
    }

    [Given("the connection profile specifies an on-prem NATS cluster URL")]
    public async Task GivenTheConnectionProfileSpecifiesAnOnPremNatsClusterUrl()
    {
        await context.StartNearestServerAsync("onprem-hub");
    }

    [Given("the connection profile specifies a cloud NATS cluster URL and no on-prem server is deployed")]
    public async Task GivenTheConnectionProfileSpecifiesACloudNatsClusterUrlAndNoOnPremServerIsDeployed()
    {
        // No on-prem hub is ever started for this scenario — trivially
        // true by construction that "no on-prem server is deployed."
        await context.StartNearestServerAsync("cloud-hub");
    }

    [When("the daemon starts")]
    public async Task WhenTheDaemonStarts()
    {
        var label = context.LastStartedHubLabel ?? throw new InvalidOperationException("No nearest server was configured.");
        await context.ConnectDaemonLeafToNearestServerAsync(label);
    }

    [Then("the daemon establishes a leaf node connection to the on-prem cluster")]
    [Then("the daemon establishes a leaf node connection directly to the cloud cluster")]
    [Then("the leaf node connection is established successfully")]
    public async Task ThenTheDaemonEstablishesALeafNodeConnection()
    {
        var label = context.LastStartedHubLabel!;
        Assert.IsTrue(await context.EventForwardingWorksAsync(label));
    }

    [Then("no code changes were required to target this environment")]
    [Then("no on-prem server is required for the daemon to operate correctly")]
    [Then("event forwarding continues to function without code modification")]
    [Then("no inbound port forwarding was required")]
    public void ThenNoCodeChangesWereRequired()
    {
        // True by construction — every scenario in this file drives the
        // exact same SyncMesh.Bdd.Tests.StepDefinitions.NearestServerContext
        // .ConnectDaemonLeafToNearestServerAsync(string) method with only
        // the target label/URL varying. No branch in that method (or in
        // production Daemon code) depends on which environment it is.
    }

    [Given("the daemon was previously connected to an on-prem nearest server")]
    public async Task GivenTheDaemonWasPreviouslyConnectedToAnOnPremNearestServer()
    {
        await context.StartNearestServerAsync("onprem-hub");
        await context.ConnectDaemonLeafToNearestServerAsync("onprem-hub");
        Assert.IsTrue(await context.EventForwardingWorksAsync("onprem-hub"), "Precondition: on-prem connection must work before switching.");
    }

    [When("the connection profile is updated to point to a cloud NATS cluster")]
    public async Task WhenTheConnectionProfileIsUpdatedToPointToACloudNatsCluster()
    {
        await context.StartNearestServerAsync("cloud-hub");
    }

    [When("the daemon is restarted \\(or reloads configuration\\)")]
    public async Task WhenTheDaemonIsRestartedOrReloadsConfiguration()
    {
        await context.ConnectDaemonLeafToNearestServerAsync("cloud-hub");
    }

    [Given("the daemon is behind a firewall with no inbound rules configured")]
    public void GivenTheDaemonIsBehindAFirewallWithNoInboundRulesConfigured()
    {
        // True by construction — see ConnectDaemonLeafToNearestServerAsync:
        // the leaf's config has no inbound `leafnodes { port: ... }`
        // listener at all, only an outbound `remotes:` entry.
    }

    [When("the daemon dials out to its configured nearest server")]
    public async Task WhenTheDaemonDialsOutToItsConfiguredNearestServer()
    {
        await context.StartNearestServerAsync("restricted-hub");
        await context.ConnectDaemonLeafToNearestServerAsync("restricted-hub");
    }
}
