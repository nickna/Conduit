# ADR 0006: Keep production persistence on JIT while isolating an AOT path

- Status: Accepted
- Date: 2026-08-11
- Decision owners: database/runtime maintainers
- Related: #1368, #1373

## Context

EF Core NativeAOT query execution is experimental and not recommended for production.
Conduit has 37 files that directly compose EF queries, including caller-supplied
expressions, conditional include/filter graphs, reporting aggregates, raw PostgreSQL
SQL, and custom JSONB/value conversions. A successful native link cannot demonstrate
correct translation, transaction, retry, or concurrency behavior.

The epic baseline produced ~102 MB Admin and ~103 MB Gateway native executables and
hundreds of first-party diagnostics. It did not execute the production database
workload. The detailed query inventory is in
`docs/operations/persistence-aot-query-inventory.md`.

## Decision

Production Gateway and Admin remain framework-dependent JIT applications for database
work. Native images must not be promoted while EF's path is experimental or until the
inventory's exit criteria are met.

Schema mutation moves to the standalone `ConduitLLM.Migrator` executable. Web services
perform only a lightweight read of the latest `__EFMigrationsHistory` row and compare
it with their compiled expected version; they never enumerate or execute migrations.
EF design and MSBuild tooling remain opt-in and absent from normal runtime publishes.

We retain EF repositories for production and maintain an isolated typed-Npgsql
NativeAOT probe as the independently verifiable fallback. We do not commit to replacing
EF based on library claims or the probe alone.

## Options considered

1. **Adopt EF compiled models/query precompilation now.** Rejected for production:
   upstream support is experimental and dynamic query coverage is incomplete.
2. **Replace repositories with typed Npgsql immediately.** Rejected: migration cost is
   high (37 direct-query files and 344 operator sites), and parity is not yet proven.
3. **Remain JIT, isolate mutation, and preserve measured options.** Accepted. It lowers
   service risk now and provides executable evidence for a later decision.

## Operational risks and rollback

- The compiled schema-version constant must change with every new migration. Tests and
  review own this invariant; a mismatch holds readiness at 503.
- The one-shot migrator is a release gate. Failure leaves existing services running and
  stops promotion before new hosts start.
- Rollback is to redeploy the previous JIT service images. Database migrations continue
  to follow forward-compatible expand/contract rules; application rollback never runs
  a down migration automatically.

## Measurements and next estimate

The existing native executable baseline remains ~102–103 MB; no production database
benchmark is accepted because the native EF workload is not proven. On 2026-08-11 the
isolated typed-Npgsql probe published for `win-x64` in 21 seconds as a 10.16 MiB native
executable and passed against PostgreSQL 16. This measures the fallback seam, not either
service or the EF workload. Replacing EF would touch at least the 37 inventoried query-owning files plus
transaction and test infrastructure, so it is a multi-phase migration rather than a
package swap. A new ADR is required before any production-native promotion.

The parallel candidate-image and deferred-promotion procedure is documented in
`docs/operations/native-aot-canary.md`; it does not supersede this decision.
