# EF Core migration tools

Use the repository-pinned .NET and EF tools from PowerShell 7+. Normal service
builds exclude EF design tooling, so prepare the Configuration project first:

```powershell
dotnet tool restore
$env:ConduitEfTooling = 'true'
dotnet build Shared/ConduitLLM.Configuration
$env:DATABASE_URL = 'postgresql://conduit:conduitpass@localhost:5432/conduitdb'
```

Remove `ConduitEfTooling` from the shell environment when finished.

## Validate migrations

```powershell
./scripts/migrations/validate-migrations.ps1
./scripts/migrations/validate-migrations.ps1 -CheckPending
./scripts/migrations/validate-migrations.ps1 -CheckPending -GenerateScript
```

Every mode obtains the authoritative inventory from `dotnet ef migrations list
--no-build --no-connect`. A failed, empty, or timed-out inventory stops validation;
filesystem enumeration never substitutes additional migrations. Increase
`-InventoryTimeoutSeconds` (default 30, maximum 600) for a slow local machine.
The EF output is retained in `artifacts/migrations/inventory.log`.

Active migration sources must match that inventory and have designers. The three
historical drafts without discoverable metadata are preserved as noncompiled text
in [Migrations/Archive](../../Shared/ConduitLLM.Configuration/Migrations/Archive/README.md).
They were deliberately archived without introducing historical migration IDs,
changing the schema gate, or changing deployment SQL.

`-CheckPending` also requires successful pending-model verification. Its timeout
can be configured with `-PendingTimeoutSeconds`, and diagnostics are saved to
`artifacts/migrations/pending-model.log`. Without `-CheckPending`, an unsuccessful
pending-model probe produces a warning. `-GenerateScript` generates a timestamped
SQL file in the repository root and rejects a failed EF process, even if it wrote
a partial file. The required CI workflow also generates an idempotent deployment
script artifact.

## Use the EF wrapper

Run the wrapper from the Configuration project. EF options belong inside the
explicit `-Command` array so PowerShell does not bind options such as `-o` as its
own common parameters. Each array element is passed as one process argument,
including paths containing spaces.

```powershell
cd Shared/ConduitLLM.Configuration
../../scripts/migrations/ef-wrapper.ps1 -Command @('migrations', 'list', '--no-build', '--no-connect')
../../scripts/migrations/ef-wrapper.ps1 -Command @('migrations', 'script', '--no-build', '-o', 'output with spaces.sql')
../../scripts/migrations/ef-wrapper.ps1 -Command @('migrations', 'add', 'MigrationName')
```

The wrapper requires `DATABASE_URL`, validates the working directory and EF tool,
and reports connectivity and command errors. SQL generation and `--no-connect`
inventory do not require a running database; a failed TCP probe warns and lets EF
handle the requested operation.

## Regression checks

These checks require PowerShell 7; the wrapper binding check also invokes the
installed EF tool's version command, but neither applies migrations nor calls a
paid provider:

```powershell
./scripts/migrations/test-bounded-command.ps1
./scripts/migrations/test-migration-inventory.ps1
./scripts/migrations/test-ef-wrapper.ps1
```

The migration-validation CI workflow runs these checks. The optional
`test-migration-tools.ps1` exercises the local environment and wrapper diagnostics;
it performs read-only migration listing and never creates migrations.
