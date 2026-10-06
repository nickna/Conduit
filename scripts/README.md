# ConduitLLM scripts

PowerShell 7 is the supported scripting environment. Run commands from the
repository root unless a command says otherwise.

## Supported workflows

| Purpose | Command |
| --- | --- |
| Start local development | `./scripts/dev.ps1` |
| Rebuild local containers | `./scripts/dev.ps1 -Build` |
| Reset local volumes (destructive) | `./scripts/dev.ps1 -Clean` |
| Re-import checked-in provider model catalogs | `./scripts/dev.ps1 -SeedModelCatalog` |
| Work in the WebAdmin container | `./scripts/dev/dev-workflow.ps1 <command>` |
| Check TypeScript projects | `./scripts/test/check-typescript.ps1` |
| Check ESLint (also used by pre-push) | `./scripts/test/validate-eslint-strict.ps1` |
| Run .NET tests | `./scripts/test/tests.ps1` |
| Run the full local build/test/coverage flow | `./scripts/test/ci-build-test.ps1` |
| Produce or inspect detailed coverage | `./scripts/test/coverage-dashboard.ps1 run|report|summary` |
| Validate EF Core migrations | `./scripts/migrations/validate-migrations.ps1 -CheckPending` |
| Apply release migrations locally | `docker compose run --rm migrate` |
| Regenerate committed Wolverine adapters | `./scripts/generate-wolverine-code.ps1` |
| Verify committed Wolverine adapters | `./scripts/generate-wolverine-code.ps1 -Verify` |
| Run the Gateway/Admin messaging smoke test | `./scripts/test/wolverine-two-host-smoke.ps1` |
| Run local CodeQL analysis | `./scripts/test/test-codeql.ps1` |
| Run the analyzer-only NativeAOT audit | `./scripts/aot/aot-audit.ps1` |

`dev.ps1` is the canonical development entry point. It always combines
`docker-compose.yml` with `docker-compose.dev.yml`; do not invoke the
development override file by itself. Its one-shot `migrate` service applies EF
and Wolverine schema changes before either API starts. When its database has no
model identifiers, the release's embedded provider catalogs are seeded by that
migrator. Use `-SeedModelCatalog` to ask the running Admin API to merge
the embedded snapshot again after rebuilding with catalog changes.

## Optional maintenance tools

- `dev/create-test-virtual-key.ps1` creates a test key using a locally running
  Admin API and `CONDUIT_MASTER_KEY`.
- `dev/fix-webadmin-errors.ps1` runs focused local lint/build remediation.
- `dev/setup-r2-dev.ps1` validates local R2 configuration before starting the
  normal development stack.
- `setup/wait-for-services.ps1` waits for the Compose services started by the
  development workflow.
- `test/test-workflows-with-act.ps1` runs selected GitHub Actions jobs locally
  when `act` is installed.
- `test/validate-workflows.ps1` checks active workflow syntax and script paths.
- `migrations/ef-wrapper.ps1`, `fix-production-migrations.ps1`, and
  `reset-dev-migrations.ps1` are specialist migration tools. The latter two can
  affect real data; use the deployment/migration runbooks before invoking them.

## Database catalog tools

`db/functions`, `db/providers`, and `db/replicate` contain SQL/catalog
generators and their input data. Provider JSON files are embedded into the shared
Configuration assembly at build time; `dev/seed-model-catalog.ps1` invokes the
same preserve-only Admin API importer used by WebAdmin. OpenRouter's runtime
metadata sync remains the preferred ongoing source for OpenRouter updates.

## Removed legacy helpers

Legacy shell entry points, hard-coded container-name utilities, obsolete
WebAdmin virtual-key bootstrap scripts, duplicate coverage helpers, and stale
test-data cleanup scripts were removed. Use the supported commands above rather
than reintroducing wrappers around the old architecture.
