# Native Gateway process fixtures

This BCL-based probe and its HTTP provider are published with NativeAOT by the
Linux gate. Production credential protection is prepared separately by the
managed `ConduitLLM.SchemaStartupFixtures` utility.

`NativeFunctionFixture` prepares a GUID-owned Exa configuration with generated
positive IDs, a separate virtual key and group, a scoped encrypted credential,
an IP whitelist, and an active flat execution price of `0.12500001`. It uses the
canonical cost mapping and leaves the existing shadow mapping null. Real
PostgreSQL tests use the production EF schema in an isolated namespace to verify
the stored values, rollback, cancellation, and independent-owner preservation.

The closed MCP fixture profile uses the same owned graph and generated positive
IDs with provider type `Mcp`, an exact loopback HTTP `/mcp` endpoint, and explicit
settings allowing only `native_search` and the fixture's private network. Exa
keeps its original null settings and authority-only endpoint. Reads and cleanup
reject the wrong profile, altered settings, invalid MCP endpoints and cross-owner
references before deleting rows. Real PostgreSQL and Redis tests cover both
profiles, transactional rollback, cancellation and preservation of unrelated
state. This fixture profile prepares data for a subsequent Gateway proof.

Stop the fixture's hosts before deleting its rows. Cleanup verifies the complete
owned graph and rejects unrelated references through canonical or shadow foreign
keys, including unsupported virtual-key cascade children. It deletes only that
owner's rows and is safe to repeat after successful deletion.

The parity harness generates the owner before seeding and passes a production-
protected credential to `--seed`. After all owned processes stop and are reaped,
`--cleanup-function-fixture` validates the graph, deletes its exact Redis keys and
delimited key families, then removes SQL rows. A Redis failure leaves SQL ownership
available for retry. Shared key rings, catalog keys, streams, and unrelated owner
state are preserved. Cleanup failure fails the gate and parent environment values
are restored in all cases.

The provider's `/search` route validates `x-api-key`, accepts generated chunked
JSON from the production Exa client, returns a structured Unicode result, and
has separate function counters. It omits `costDollars` so the execution proof must
use configured pricing. A query containing `native-function-error` returns a real
provider 400 for failure-path verification.

The provider also has a stateless, non-streaming agentic fixture selected by a
GUID-marked prompt. Its first reply derives the function name from the actual
advertised query schema and requests one search. A final reply requires the
matching assistant tool call, exact arguments and the complete rich Exa result.
Separate phase and rejection counters leave ordinary chat and SSE counters
unchanged. Real HTTP tests exercise production OpenAI-compatible and Exa clients;
this provider fixture alone does not prove the Gateway agentic workflow.

`NativeMcpProviderStub` is an instance-owned loopback TCP fixture for the pinned
MCP client's stateless `2026-07-28` discovery, listing and tool-call transport. It
checks actual authentication, protocol headers and request metadata, and returns
Unicode schema/text with structured results. Received-request latches make
cancellation checks independent of timing; disposal stops the listener, closes
owned sockets and joins its handlers within a fresh deadline. Production-client
tests also cover filtering, routing arguments, endpoint selection and bounded
large responses. This fixture does not establish the Gateway's native MCP
workflow, legacy session negotiation or streaming transport support.

The PostgreSQL precision test records the existing six-decimal request-log,
spend-history, and ledger columns alongside eight-decimal function costs and group
balances. Billing verification must compare each persisted value at its actual
column precision.

The complete harness explicitly sets `CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS=1000`
for its owned hosts because every phase shares one loopback IP. IP limiting stays
enabled, and the fixture keys retain their separate RPM/RPD limits. The tracked
environment setting is restored during cleanup.
The inherited base/derived option mismatch is tracked in
[#1580](https://github.com/nickna/Conduit/issues/1580).

`NativeFunctionExecutionProbe` runs after the existing protocol checks. It sends
guarded chat requests through both aliases on both Gateways before any execution,
using the separate owned key. Authentication and missing/disabled IDs precede
iteration validation. The complete provider counter snapshot and owned accounting
stay unchanged through several spend flush windows, and the full host's correlation
header remains the caller's value before billing setup. These guarded requests
check preflight before the successful workloads.

The probe then sends
one successful search and one real provider rejection through each Gateway, reads
all four executions through the opposite host, and compares the HTTP contracts
against an independent 24-column SQL oracle. A separate key/group isolates the
proof's pricing, two successful request logs, balance, and settled ledger totals.
Ownership denial, missing executions, unauthenticated reads, and unbilled failures
are checked too. The published two-host process gate must pass before this work
can establish native function execution support.

The final phase runs one automatic Exa search followed by a final LLM reply on
both chat aliases and both Gateways. Each request carries its own GUID-marked
prompt. The provider requires the advertised schema and matching assistant/tool
messages; the probe checks aggregate token usage, execution summaries, all 24
persisted execution fields, opposite-host reads and ownership denial. Four chats
produce four combined LLM/function charges alongside the two inline charges.
Independent SQL checks preserve the distinct execution/group and log/ledger
precision and require stable accounting through multiple spend flushes. Response
cost summaries and iteration values retain their inherited behavior; the SQL
charge is checked separately. These cost summaries, zero iteration and omitted
chat-log function metadata are tracked in
[#1577](https://github.com/nickna/Conduit/issues/1577),
[#1578](https://github.com/nickna/Conduit/issues/1578) and
[#1579](https://github.com/nickna/Conduit/issues/1579). This bounded non-streaming Exa workflow does not
establish MCP, provider-hosted tools, agentic SSE, parallel/dependent calls or
function audit/retention support. Published Linux verification remains required.

The probe preserves the current provider-error diagnostics in failed POST/GET
responses; parity does not establish that these diagnostics are suitable for
External error mode. The six-versus-eight decimal reporting discrepancy is tracked
in [#1574](https://github.com/nickna/Conduit/issues/1574).
