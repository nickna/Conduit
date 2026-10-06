# Database Migration Infrastructure

Release migration and runtime readiness handling for the single EF Core context
(`ConduitDbContext`, PostgreSQL only). Normal services are read-only with respect
to schema; behavior is governed by `CONDUIT_MIGRATION_MODE` — the full
operational guide is
[docs/operations/deployment/migration-deployment-strategy.md](../../../docs/operations/deployment/migration-deployment-strategy.md),
and the schema-compatibility rules are
[ADR-002](../../../docs/architecture/adr-002-expand-contract-migration-policy.md).

## Components

| File | Role |
|---|---|
| `MigrationStartupOptions.cs` | `CONDUIT_MIGRATION_MODE` (`Wait`/`Skip`) + timeout env parsing; rejects legacy `Apply` |
| `SimpleMigrationService.cs` | Applies migrations + seeds default data under a blocking session-scoped `pg_advisory_lock(7891011)` |
| `MigrationWaitService.cs` | Wait mode: compares the latest history row with the compiled schema version; flips readiness when current |
| `MigrationReadinessState.cs` | Process-wide "schema is current" flag |
| `../HealthChecks/PendingMigrationsReadinessCheck.cs` | Gates `/health/ready` (tag `ready`) on that flag |
| `MigrationExtensions.cs` | Read-only readiness registration (`AddDatabaseMigration`) |
| `tools/ConduitLLM.Migrator` | Standalone EF migrator + Wolverine schema provisioning (release-hook entry point) |
| `ExecutionStrategyExtensions.cs` | `ExecuteInTransactionAsync` — explicit transactions compatible with `EnableRetryOnFailure` |
| `ConfigurationDbContextFactory.cs` | Design-time factory; also resolves `DATABASE_URL` for the standalone migrator |

## Design invariants

- **Migrations only.** Never `EnsureCreated` — it bypasses `__EFMigrationsHistory` and
  historically corrupted production databases
  (see `scripts/migrations/fix-production-migrations.ps1`).
- **One session for lock + migration.** The advisory lock and `MigrateAsync` share a
  dedicated `NpgsqlConnection`; if the migrator dies, the lock dies with the
  connection. The migration context deliberately has no retrying execution strategy.
- **Losers block, then verify.** Contending instances block on `pg_advisory_lock`
  (waiter timeout `CONDUIT_MIGRATION_LOCK_TIMEOUT_SECONDS`), then re-check
  `GetPendingMigrationsAsync()` and find nothing to do.
- **Seeding runs under the lock** and is idempotent.
- **Web processes are schema-read-only.** Only `ConduitLLM.Migrator` constructs
  `SimpleMigrationService` or enables Wolverine resource provisioning.
