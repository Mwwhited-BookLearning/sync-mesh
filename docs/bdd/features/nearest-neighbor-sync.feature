# Use cases: docs/09-use-cases.md#uc2--select-nearest-server-via-configuration
#            docs/09-use-cases.md#uc3--reconcile-event-history-across-sites
# Diagrams: docs/sequence-diagrams.md > "Event Recording Flow — Local App to Nearest Server"
#           docs/sequence-diagrams.md > "Server Mesh Reconciliation — HLC-Ordered, Idempotent Apply"
#           docs/08-deployment-models.md > all six topology shapes (#2–#6, and #1 for the isolated case)
Feature: Nearest-neighbor sync with configuration-driven environment selection
  As an operator deploying the daemon in different environments
  I want the "nearest server" to be selected via configuration
  So that switching between on-prem, WAN, and cloud requires no code changes

  Background:
    Given the daemon is configured with a nearest-server connection profile

  Scenario: Daemon connects to an on-prem nearest server
    Given the connection profile specifies an on-prem NATS cluster URL
    When the daemon starts
    Then the daemon establishes a leaf node connection to the on-prem cluster
    And no code changes were required to target this environment

  Scenario: Daemon connects directly to a cloud nearest server with no on-prem tier
    Given the connection profile specifies a cloud NATS cluster URL and no on-prem server is deployed
    When the daemon starts
    Then the daemon establishes a leaf node connection directly to the cloud cluster
    And no on-prem server is required for the daemon to operate correctly

  Scenario: Switching from on-prem to cloud is a configuration change only
    Given the daemon was previously connected to an on-prem nearest server
    When the connection profile is updated to point to a cloud NATS cluster
    And the daemon is restarted (or reloads configuration)
    Then the daemon establishes a leaf node connection to the cloud cluster
    And event forwarding continues to function without code modification

  Scenario: Daemon connectivity survives firewall/NAT without inbound rules
    Given the daemon is behind a firewall with no inbound rules configured
    When the daemon dials out to its configured nearest server
    Then the leaf node connection is established successfully
    And no inbound port forwarding was required

  Scenario: A standalone server with no peer connections operates correctly and permanently
    Given a server has no gateway connections configured to any peer
    When daemons connect to it and forward events
    Then the server durably stores and serves those events as a complete system of record on its own
    And this is a first-class, permanent deployment mode, not a bootstrapping step toward a mesh

  Scenario: Server mesh reconciles events from multiple sites
    Given Server A and Server B are connected via a gateway/supercluster connection
    When Server A receives a new event from its local daemon
    Then Server B eventually receives and applies the same event
    And Server A eventually receives and applies any event Server B produces locally, the same way
    And the reconciliation does not require synchronous coordination between A and B

  Scenario: Servers within a site are fully meshed by default; cross-site links use a limited gateway
    Given multiple servers are deployed at the same site
    And a separate site (or cloud region) is also deployed
    When gateway connections are configured
    Then the servers within the same site are connected to each other directly (full mesh)
    And only a single or limited set of designated gateway servers per site carries the cross-site connection
    And every server at every site still converges to the same fully-replicated event history

  Scenario: Full mesh is equally valid extending directly to cloud or remote sites
    Given an operator chooses to configure every server, on-prem and cloud alike, as a direct gateway peer of every other server
    When this topology is deployed
    Then it is fully supported, with no architectural minimum or maximum on server or site count
    And it converges to the same fully-replicated event history as the limited-gateway pattern
