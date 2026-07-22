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

  Scenario: Server mesh reconciles events from multiple sites
    Given Server A and Server B are connected via a gateway/supercluster connection
    When Server A receives a new event from its local daemon
    Then Server B eventually receives and applies the same event
    And the reconciliation does not require synchronous coordination between A and B
