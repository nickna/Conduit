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

Stop the fixture's hosts before deleting its rows. Cleanup verifies the complete
owned graph and rejects unrelated references through canonical or shadow foreign
keys, including unsupported virtual-key cascade children. It deletes only that
owner's rows and is safe to repeat after successful deletion.

The provider's `/search` route validates `x-api-key`, accepts generated chunked
JSON from the production Exa client, returns a structured Unicode result, and
has separate function counters. It omits `costDollars` so the execution proof must
use configured pricing. A query containing `native-function-error` returns a real
provider 400 for failure-path verification.

The PostgreSQL precision test records the existing six-decimal request-log,
spend-history, and ledger columns alongside eight-decimal function costs and group
balances. Billing verification must compare each persisted value at its actual
column precision.

These fixture tests do not establish native function execution support. The
published two-host HTTP, persistence, and billing proof remains a separate gate.
