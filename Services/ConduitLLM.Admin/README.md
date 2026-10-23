# ConduitLLM Admin API

The Admin API manages Conduit's providers, virtual keys, model mappings, costs,
routing, and media lifecycle. Operators and the WebAdmin UI use this service;
inference clients use the separate [Gateway API](../ConduitLLM.Gateway/).

Start with the root [architecture and quick start](../../README.md),
[configuration guide](../../docs/configuration.md), and
[API contract guide](../../docs/api-guides/openapi-maintenance.md).

## Configuration and prerequisites

- For a source checkout, install the .NET 10 SDK selected by
  [`global.json`](../../global.json) (currently `10.0.400`, with the configured
  roll-forward policy). The services target `net10.0`.
- Set `DATABASE_URL` to a reachable PostgreSQL database. PostgreSQL is the only
  supported database; Wolverine messaging also uses it.
- Set `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` to the shared backend/master secret.
  This is the key used for operator access to the Admin API.
- Redis is optional for a standalone Admin instance. Configure `REDIS_URL` for
  shared ephemeral-key storage and Redis-backed provider-error monitoring. The
  Gateway and the full local stack require Redis.

Set deploy-time configuration through environment variables. The repository
ships no `appsettings.json`; use double underscores for hierarchical settings,
such as `AdminApi__AllowedOrigins__0=http://localhost:3000`.
The annotated [`.env.example`](../../.env.example) and
[configuration guide](../../docs/configuration.md) describe supported settings.
Provider credentials and other runtime configuration are stored in PostgreSQL
and managed through WebAdmin or this API.

## Running the Admin API

Run the commands below from the **repository root**. For the complete stack,
follow the root [Docker Compose quick start](../../README.md#run-with-docker-compose)
or [development instructions](../../README.md#development-environment), including
environment setup. The maintained local stack publishes Admin at
**http://localhost:5002**; the container listens on port **8080**.

For a direct source run, start PostgreSQL first and set variables in the invoking
shell. `dotnet run` does not load the repository's `.env` file automatically.
For example, with the reference local database credentials and published port:

```powershell
$env:DATABASE_URL = 'postgresql://conduit:conduitpass@localhost:5432/conduitdb'
$env:CONDUIT_API_TO_API_BACKEND_AUTH_KEY = 'replace-with-your-secure-backend-key'
$env:ASPNETCORE_URLS = 'http://localhost:5002'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:CONDUIT_ENABLE_HTTPS_REDIRECTION = 'false' # Local HTTP development only

dotnet run --project tools/ConduitLLM.Migrator
```

Only after the migrator exits successfully, start the API in the same shell:

```powershell
dotnet run --project Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj
```

The standalone migrator applies EF migrations, seeds initial data, and provisions
Wolverine storage. Neither API changes the schema during normal startup. The
default `CONDUIT_MIGRATION_MODE=Wait` keeps `/health/ready` unhealthy until the
schema is current. Compose runs its one-shot `migrate` service before starting
the APIs. See the
[migration deployment strategy](../../docs/operations/deployment/migration-deployment-strategy.md)
for release sequencing and recovery.

## Authentication and API documentation

Use the master key in the **`X-Master-Key`** header, as defined by the
authoritative [`openapi-admin.json`](./openapi-admin.json) contract. For example:

```bash
curl http://localhost:5002/v1/admin/virtual-keys \
  -H 'X-Master-Key: YOUR_MASTER_KEY'
```

WebAdmin signs administrators in through Clerk and obtains short-lived ephemeral
master keys for browser requests. See the root
[authentication overview](../../README.md#configuration) for the credential roles.

The contract is the source of truth for current routes, request/response shapes,
and security requirements. In **Development**, the host exposes
`/openapi/v1.json` and Scalar interactive documentation at `/scalar/v1`.
Production consumers can use the committed contract. Follow the
[API contract guide](../../docs/api-guides/openapi-maintenance.md) for usage,
client generation, and contract maintenance.

## Development

Minimal API route definitions live in `Endpoints/`, business logic in `Services/`,
and authentication in `Security/`. Build and run the Admin tests from the
repository root:

```bash
dotnet build Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj
dotnet test Tests/ConduitLLM.Tests/ConduitLLM.Tests.csproj --filter 'FullyQualifiedName~ConduitLLM.Tests.Admin'
```
