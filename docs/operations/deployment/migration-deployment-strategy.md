# Database migration deployment strategy

Conduit uses one explicit release step to own all schema changes. Gateway and
Admin processes do not call `Database.Migrate`, `EnsureCreated`, or Wolverine
auto-provisioning during normal startup.

## Release sequence

1. Build the Admin, Gateway, and WebAdmin candidate images.
2. Run the standalone migrator bundled in the candidate Admin image.
3. Only after exit code `0`, roll out Gateway and Admin (and promote any floating
   image tags).

The migrator applies EF Core migrations, imports the bundled model catalog on an
empty installation, and provisions Wolverine's PostgreSQL schemas. A non-zero
exit code must stop the rollout. Existing services remain available because
they have not been updated yet.

The tag-triggered GitHub Actions release workflow implements this sequence.
Configure the `production` GitHub Environment with a
`CONDUIT_RELEASE_DATABASE_URL` secret that is reachable from its runner. Candidate
images are pushed under a `-candidate` suffix; version and `latest`/`beta` tags
are not changed until the migration succeeds.

## Commands

For the repository's Compose deployment, one command performs the migration:

```bash
docker compose run --rm migrate
```

`docker compose up -d` also runs the same one-shot service automatically and
starts both APIs only after it succeeds.

For another container platform, run the release's standalone migrator as a Job or
release hook:

```bash
docker run --rm \
  --entrypoint dotnet \
  -e DATABASE_URL \
  -e CONDUIT_MIGRATION_LOCK_TIMEOUT_SECONDS=600 \
  ghcr.io/nickna/conduit-admin:<version> /app/migrator/ConduitLLM.Migrator.dll
```

For a source checkout:

```bash
dotnet run --project tools/ConduitLLM.Migrator
```

Both normal services should use `CONDUIT_MIGRATION_MODE=Wait` (the default) and
`ConduitLLM__Messaging__Wolverine__AutoProvision=false` (also the default).
`Wait` performs a read-only schema-version probe and returns `503` from
`/health/ready` until the schema is current. The readiness message tells the
operator to run the explicit migrator. `Skip` disables this guard for tests or a
deliberate break-glass operation. The removed `Apply` value fails startup with an
actionable error instead of silently mutating the database.

## Retries and concurrency

The command is idempotent. It checks EF's migration history on every run, so a
retry after success is a no-op apart from idempotent seed/provisioning work.

EF migration and seed work is serialized by the session-scoped PostgreSQL
advisory lock `7891011`. A second invocation waits, then rechecks the migration
history after the winner finishes. Wolverine resource setup is idempotent, so
concurrent invocations are harmless. Set
`CONDUIT_MIGRATION_LOCK_TIMEOUT_SECONDS` to bound how long a contender waits;
`0` waits indefinitely.

If the migrator fails, inspect its logs, correct the database or network problem,
and rerun the same command. Do not start a rollout with
`CONDUIT_MIGRATION_MODE=Skip` to bypass a failed release migration.

## Validation

The migration validation workflow runs the release command against:

- an empty database;
- a database at the previous migration;
- an already-current database (safe retry);
- two concurrent invocations; and
- an unreachable database (non-zero release gate).

It also checks for pending model changes and uploads an idempotent SQL script for
DBA review or manual recovery.
