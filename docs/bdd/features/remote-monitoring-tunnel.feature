Feature: Remote monitoring and tunnel fallback via nearest server
  As a remote monitoring user
  I want to observe or interactively access a recording instance directly when possible
  So that I have low-latency access, with a reliable fallback when firewalls block direct connectivity

  Background:
    Given a recording instance is actively recording
    And a remote user has valid credentials to monitor or tunnel to it

  Scenario: Direct tunnel connection succeeds when network allows it
    Given the remote user's client can reach the daemon directly
    When the remote user requests an interactive session
    Then the session is established directly, without relaying through the nearest server

  Scenario: Tunnel falls back to relay when direct connection is blocked
    Given the remote user's client cannot reach the daemon directly (firewall/NAT)
    When the remote user requests an interactive session
    Then the client attempts direct connection first
    And upon failure, falls back to relaying through the nearest server
    And the session is established via the relay

  Scenario: Passive monitoring works regardless of tunnel path availability
    Given the interactive tunnel path is currently blocked and no relay session is active
    When the daemon publishes telemetry to its monitor subject
    Then the remote user still receives monitoring data via the existing event-mesh routing

  Scenario: Tunnel/relay outage does not affect event sync
    Given the tunnel relay mechanism is unavailable or failing
    When the local daemon continues recording and forwarding events
    Then event durability and forwarding to the nearest server are unaffected
    And no event-sync component depends on the tunnel/relay mechanism being healthy

  Scenario: Event-sync outage does not affect monitoring/tunnel
    Given the event-sync mesh is degraded or unavailable
    When a remote user requests a direct or relayed interactive session
    Then the tunnel/monitoring path functions independently of event-sync health
