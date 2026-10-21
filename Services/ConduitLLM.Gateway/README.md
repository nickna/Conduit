# ConduitLLM.Gateway

`ConduitLLM.Gateway` is Conduit's OpenAI-compatible inference API. It authenticates
virtual keys, routes model aliases to upstream providers, streams responses, and
tracks usage and spending. It serves chat, embeddings, images, video, audio, and
server-side function execution. Configuration and key management belong to the
separate [Admin API](../ConduitLLM.Admin/) and WebAdmin UI.

See the root [architecture and repository layout](../../README.md) and
[core concepts](../../docs/concepts.md) for the service boundaries and request
lifecycle.

## Configuration

The Gateway reads process settings from **environment variables only**. Its host
clears the default configuration sources; `appsettings.json` does not configure
the running service. Hierarchical settings use double underscores, for example
`Logging__LogLevel__Default=Information`.

The annotated [`.env.example`](../../.env.example) and maintained
[configuration guide](../../docs/configuration.md) describe the supported settings.
The essential infrastructure settings are:

| Variable | Purpose |
| --- | --- |
| `DATABASE_URL` | Required PostgreSQL URI or Npgsql connection string. PostgreSQL is the only supported database. |
| `REDIS_URL` | Required for the normal Gateway runtime, including a single instance. `CONDUIT_REDIS_CONNECTION_STRING` is the fallback when `REDIS_URL` is absent or blank. |
| `ASPNETCORE_URLS` | Listen URLs, for example `http://localhost:5000` when running directly. |
| `ASPNETCORE_ENVIRONMENT` | Defaults to `Production`; use `Development` to expose interactive API documentation. |
| `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` | Shared secret for backend service-to-service authentication; see the configuration guide for authentication setup. |

Redis supports caching, rate limiting, spending coordination, ephemeral keys, and
the SignalR backplane. Internal messaging uses Wolverine over PostgreSQL; no
separate message broker is needed. Persist and back up PostgreSQL through your
database deployment. The reference [Compose file](../../docker-compose.yml) uses
the `pgdata` and `redis-data` named volumes for PostgreSQL and Redis respectively.

Provider credentials, model mappings, costs, and virtual keys are stored in the
database and managed through WebAdmin or the Admin API. See
[runtime configuration](../../docs/configuration.md#runtime-configuration-in-the-admin-ui).

## Running the Gateway

Run commands below from the **repository root**.

### Full development stack

Follow the root [development environment instructions](../../README.md#development-environment)
and use the development script:

```powershell
./scripts/dev.ps1
```

### Run directly with the .NET CLI

Install the .NET 10 SDK and start PostgreSQL and Redis before running the service.
Set environment variables in the invoking shell; `dotnet run` does not load the
repository's `.env` file automatically. For local infrastructure using the
reference database credentials and published ports:

```powershell
$env:DATABASE_URL = 'postgresql://conduit:conduitpass@localhost:5432/conduitdb'
$env:REDIS_URL = 'redis://localhost:6379'
$env:ASPNETCORE_URLS = 'http://localhost:5000'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:CONDUIT_MEDIA_STORAGE_TYPE = 'InMemory' # Local development only

dotnet run --project tools/ConduitLLM.Migrator
dotnet run --project Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj
```

Run the Gateway only after the migrator succeeds. The standalone migrator applies
EF migrations, seeds initial data, and provisions Wolverine storage. Gateway and
Admin never change the schema during normal startup. The default
`CONDUIT_MIGRATION_MODE=Wait` keeps `/health/ready` unhealthy until the schema is
current. See the [migration deployment strategy](../../docs/operations/deployment/migration-deployment-strategy.md)
for release sequencing and failure recovery.

### Docker

Build the Gateway image with the repository root as the build context:

```bash
docker build -f Services/ConduitLLM.Gateway/Dockerfile -t conduit-http:local .
```

Follow the root [Docker Compose instructions](../../README.md#run-with-docker-compose)
to configure and start the complete deployment. Compose runs the one-shot
`migrate` service before starting the APIs. The Gateway container listens on
**8080**; Compose publishes it at **http://localhost:5000**. Container health
probes must use port 8080. Configure HTTPS termination at your ingress or reverse
proxy for production.

## API usage and documentation

Clients authenticate with a Conduit virtual key (`condt_...`) in the
`Authorization: Bearer ...` header and send a configured model alias. Create
providers, model mappings, costs, and funded virtual-key groups through the Admin
API or WebAdmin before serving inference requests.

```bash
curl http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer condt_yourvirtualkey" \
  -d '{"model":"my-model-alias","messages":[{"role":"user","content":"Hello!"}]}'
```

Standard and streaming responses are supported. See the root
[OpenAI SDK example](../../README.md#using-with-openai-sdks) for client setup.

In **Development**, the running host exposes the OpenAPI document at
`/openapi/v1.json` and Scalar interactive documentation at `/scalar/v1`. These
routes are not exposed by the normal Production host. The checked-in
[`openapi-gateway.json`](./openapi-gateway.json) is also available for contract
inspection and client generation.

## Health checks and operations

- `GET /health/live`: process liveness; use for restart decisions.
- `GET /health/ready`: readiness, including schema, messaging, and Redis checks;
  use for traffic admission and container health checks.
- `GET /health`: aggregate health for operators.

Private-network and loopback health probes are allowed; external probes require
the configured health key in `X-Conduit-Health-Key`. See
[monitoring](../../docs/monitoring.md#health-endpoints) for the health policy,
Prometheus metrics, logs, and operational guidance. Readiness returns `503` while
required dependencies or the database schema are unavailable.

Logging is configured through environment variables such as
`Logging__LogLevel__Default`. For Compose deployments, inspect Gateway logs with
`docker compose logs api`.

## License

See the root [LICENSE](../../LICENSE).
