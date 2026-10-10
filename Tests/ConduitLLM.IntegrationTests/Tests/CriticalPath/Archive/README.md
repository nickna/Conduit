# Retired external critical-path harness

The `.cs.txt` files preserve the original external harness and its fixture for
historical reference. They are not compiled or executable acceptance evidence.
The harness allowed missing providers and unobserved faults to return success,
and its API setup did not follow current mutation contracts. See
[#1464](https://github.com/nickna/Conduit/issues/1464).

The adjacent `.cs` classes contain only 30 explicit, fixture-free xUnit skip
records. They load no provider configuration, create no containers, and make no
external requests. A record fails if somebody removes its skip without first
implementing an actual acceptance scenario.

[`critical-path-map.json`](../../../../../scripts/ci/critical-path-map.json)
assigns every historical scenario to its required replacement or a justified
manual procedure. Its regression preserves all 30 names and enforces retirement.
