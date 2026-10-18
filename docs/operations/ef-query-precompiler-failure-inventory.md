# Observed EF query-precompiler failures

This index reconciles the five failed owner passes in the
[2026-10-12 isolated spike](ef-native-query-spike.md), frozen from
`b051389580a358fe7699e45f439d2c3fbc5ddce3`. It identifies observed failure
expressions and possible source owners. It does not inventory every semantic EF
query, establish native query support, or complete #1373.

## Interpretation and accounting

The original logs render a query syntax node and an exception without file or
line metadata. Locations are therefore **inferred source matches**, derived by
comparing Roslyn expression tokens within the analyzed owning project and the
72 retained lexical candidates. Token comparison omits trivia and preserves
literal token text. Ambiguous matches retain every candidate; they do not prove
that each candidate failed.

There are 730 original diagnostic renderings and 334 distinct signatures, keyed
by project, query tokens, exception type, and exception message. Of these signatures, 310 have
one inferred source match and 24 have multiple candidates. No expression parse,
record grammar, or unmatched-signature failures were observed in this pass.
Different failure messages for the same expression remain distinct signatures;
these counts are not distinct EF operations or SQL queries.

| Owner pass | Distinct failure signatures | Lexical files represented by unique inferred matches | Lexical candidate files |
|---|---:|---:|---:|
| Configuration | 241 | 38 | 40 |
| Core | 1 | 1 | 1 |
| Providers | 1 | 1 | 1 |
| Admin | 84 | 24 | 26 |
| Gateway | 7 | 3 | 4 |
| Total | 334 | 67 | 72 |

| Top-level failure family | Distinct signatures | Original renderings | Disposition |
|---|---:|---:|---|
| Dynamic query rejection | 270 | 596 | Documented experimental precompiler limitation |
| Parameter-symbol translation | 59 | 124 | Matches [upstream EF #35494](https://github.com/dotnet/efcore/issues/35494) |
| Expired-key bulk-update null reference | 1 | 2 | Focused reduction in [#1660](https://github.com/nickna/Conduit/issues/1660) |
| Other precompiler diagnostics | 4 | 8 | Focused reduction in [#1662](https://github.com/nickna/Conduit/issues/1662) |

Two of the four other forms wrap an inner null reference. Thus the one bulk-update
row is not a count of every null-reference-bearing diagnostic: three query
signatures contain a null reference, appearing in six renderings in total.

## Other observed forms

These are inferred locations in the retained source text, not locations emitted
by EF. The full exception cause remains unproven.

| Source expression location | Observed form |
|---|---|
| `Services/ConduitLLM.Admin/Services/AdminProviderSyncService.cs:89` | Parameter-evaluation wrapper with an inner null reference |
| `Services/ConduitLLM.Admin/Endpoints/ProviderToolsEndpoints.cs:308` | Parameter-evaluation wrapper with an inner null reference |
| `Services/ConduitLLM.Gateway/Endpoints/DiscoveryEndpoints.cs:199` | `ThenInclude` first-parameter translation exception |
| `Services/ConduitLLM.Gateway/Services/BillingReconciliationService.cs:88` | Unknown `CheckpointId` identifier during expression translation |

Five lexical owners have no matched failure expression in these captures:

- `Services/ConduitLLM.Admin/Endpoints/ModelEndpoints.cs`
- `Services/ConduitLLM.Admin/Services/AdminVirtualKeyService.ModelRateLimits.cs`
- `Services/ConduitLLM.Gateway/Services/DiscoveryCacheLoader.cs`
- `Shared/ConduitLLM.Configuration/Data/SimpleMigrationService.cs`
- `Shared/ConduitLLM.Configuration/ProviderService.cs`

Absence from failed diagnostics does not establish analysis, successful
precompilation, native execution, or unsupported status. The lexical scan can
also select in-memory operations and source excluded from a project's compile
items; it remains separate from a semantic query inventory.

## Repository-suite coverage gap

The spike's 224-case plan contains every declared test method in its 35 selected
concrete classes. That folder/name-based selection misses nine direct repository
Fact cases in `Tests/ConduitLLM.Tests/Integration/VirtualKeyBalanceTrackingTests.cs`.
Those tests construct `VirtualKeyGroupRepository` over SQLite and check balance
history, initial transactions, default descriptions, missing groups, reference
types, and rollback when saving the ledger fails. They were not in the native
dispatcher plan and did not execute natively. This is a concrete omission, not
an exhaustive list of missing repository tests.
The later [expanded repository spike](ef-native-repository-spike.md) adds those
nine direct bindings to a 233-case lower-bound plan. Its historical 224-case
omission remains recorded here; expanded planning does not establish execution
or complete repository coverage.

Broader service, locking, migration, endpoint, and orchestration candidates need
their own subject/call-path classification. Using a repository mock alone does
not make a test a repository-contract case. Complete that inventory and retain
each original backend before designing the full PostgreSQL native suite.

## Evidence and limits

The original diagnostic index SHA-256 is
`9ed397d75d12340ebb79d45a12d1d3f4b084c3e0a14b23c9dd4a9cce6390c2ff`.
It preserves diagnostic syntax/capture hashes, rendering counts, candidate
locations, ambiguity, source hashes, and all five original command identities.
The [public metadata index](ef-query-precompiler-failures.json) exposes all 334
signatures and their candidate source spans, references a 72-source hash table,
and retains the five capture identities. It omits raw query bodies, exception
messages/stacks, and private paths. Failure-message hashes preserve the signature
distinction; the original captures remain necessary to inspect those messages.
Locations and source hashes describe the closing scratch snapshot. Where copies
differed only by CRLF/LF, both byte hashes and an explicit newline-only comparison
are retained; those hashes are not relabeled as analyzer-time bytes.

Source matching used Roslyn 5.0 with default preprocessor symbols. This is a
token-location inference, not a reconstruction of every EF workspace property,
conditional compilation choice, reachable operation, or generated SQL plan.
No new build, EF invocation, database fixture, or test execution was performed
to create this index. All 224 planned native cases remain `NOT_EXECUTED`, and the
additional nine cases remain outside that plan. Production remains JIT.

## Independent generated-code compilation controls

These later reductions are separate from the frozen 730-rendering/334-signature
owner-pass index and do not change its counts or metadata JSON. Successful
precompilation can still emit C# that does not compile.

| Local tracker | Isolated variant | Initial build | Generation | Generated-code build | Upstream |
|---|---|---:|---:|---|---|
| [#1695](https://github.com/nickna/Conduit/issues/1695) | `ReadPredicate` | 0 | 0 | 1, `CS0030`: sealed `VirtualKey` to `IInjectableService` | [EF #37957](https://github.com/dotnet/efcore/issues/37957), open/Backlog in the 2026-10-17 UTC review |
| [#1696](https://github.com/nickna/Conduit/issues/1696) | `DiscoveryIncludeOnly` | 0 | 0 | 1, `CS1003`/`CS1002`: dotted `PrimaryKeyProperties` local | [EF #37972](https://github.com/dotnet/efcore/issues/37972), closed by [PR #39208](https://github.com/dotnet/efcore/pull/39208); supported package availability remains separate |

The fresh Linux SDK 10.0.401 / EF 10.0.12 / Npgsql 10.0.3
[control and evidence identifiers](../../tools/EfQueryPrecompilerRepro/README.md#sealed-entity-generated-compilation-tracker-1695)
retain the generated source, diagnostics and all three stage exits. No database
or native query execution occurred. No supported fixed version was identified;
the inspected 10.0.13 release was not tested. An available upstream fix must pass
generation and generated-code compilation before this tracker changes status.
Production seals, package pins and diagnostics remain intact.

The [opt-in repeat runner](../../tools/EfQueryPrecompilerRepro/README.md#sealed-entity-generated-compilation-tracker-1695)
now automates both controls' three gates in fresh copies with aligned local EF tooling.
Its default exit 0 asserts the known compilation failure, while explicit
`-Expect CompilationSuccess` requires a generated-source Release build exit 0.
The retained `result.json` binds commands, individual exits, logs, unchanged
inputs/generated sources and resolved package assets by SHA-256. Infrastructure
or generation failures do not satisfy either compilation expectation.

The [fresh Include-only control](../../tools/EfQueryPrecompilerRepro/README.md#dotted-local-name-generated-compilation-tracker-1696)
retains its own three stage exits and generated source. The fix is on upstream's
12.0.0-alpha main line; inspected 10.0.13/release-10.0 source still has the old
name construction, and neither a newer package nor fixed source was executed.
This control remains separate from `ThenInclude` analysis and sealed-entity
compilation, with no database/native result or change to the historical index.
The runner's `-Issue 1696` check additionally requires both syntax diagnostics
and the dotted local declaration in emitted C#. Its Windows SDK 10.0.401 repeat
on 2026-10-17 (America/Los_Angeles) reproduced `0/0/1`; the same-day upstream
review found no supported 10.x backport. Both trackers remain open for a future
applicable supported release and successful generated-code compilation.
