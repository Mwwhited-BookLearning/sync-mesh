Feature: <Short, capability-oriented name>
  As a <role>
  I want <capability>
  So that <business/technical value>

  Background:
    Given <shared setup for all scenarios in this feature>

  Scenario: <specific, concrete behavior>
    Given <precondition>
    And <additional precondition>
    When <action>
    Then <observable outcome>
    And <additional observable outcome>

  Scenario: <edge case or failure mode>
    Given <precondition>
    When <action that triggers the edge case>
    Then <expected safe/correct behavior — not just "it doesn't crash">
