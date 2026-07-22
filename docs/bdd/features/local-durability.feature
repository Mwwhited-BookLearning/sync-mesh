Feature: Local daemon durability during recording
  As a system operator
  I want the local daemon to durably buffer events only during an active recording session
  So that no events are lost during transient connectivity loss, without the daemon becoming a permanent store

  Background:
    Given a local daemon is running with an embedded NATS leaf node
    And the daemon's local JetStream stream uses WorkQueue retention

  Scenario: Event is retained locally until the nearest server acknowledges it
    Given the local app sends an event to the daemon
    When the nearest server is temporarily unreachable
    Then the event is durably stored in the local buffer
    And the event is not lost if the daemon process restarts
    And the event remains in the local buffer until upstream acknowledgment is received

  Scenario: Event is removed from local buffer after upstream acknowledgment
    Given an event has been durably stored in the local buffer
    When the nearest server acknowledges receipt of the event
    Then the event is removed from the local buffer
    And the local buffer does not grow unbounded over the course of a recording session

  Scenario: Local buffer respects its configured capacity cap
    Given the local buffer has a configured MaxAge and MaxMsgs cap
    When the nearest server is unreachable for longer than expected
    And the buffer reaches its configured cap
    Then the system surfaces an explicit operational warning
    And the behavior on cap overflow is a deliberate, documented decision (not silent data loss)

  Scenario: Recording session ends and buffer is not treated as a system of record
    Given a recording session has ended
    And all events from that session have been acknowledged upstream
    Then the local buffer contains no residual events from that session
    And no component depends on the local buffer for historical event retrieval
