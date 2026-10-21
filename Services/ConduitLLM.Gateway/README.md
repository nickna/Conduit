# ConduitLLM.Gateway

## Overview

`ConduitLLM.Gateway` is the HTTP API backend for the Conduit solution (`Conduit.slnx`). It exposes a unified, OpenAI-compatible REST API for interacting with multiple Large Language Model (LLM) providers such as OpenAI, Anthropic, Azure OpenAI, Gemini, Cohere, and others. It acts as the main programmatic interface for client applications and the ConduitLLM WebAdmin frontend.

## Role in the Conduit Solution

- **Conduit.slnx**: The overall solution file tying together all Conduit sub-projects.
- **ConduitLLM.Gateway**: This project. Provides the HTTP API for LLM access, model routing, API key/virtual key management, and provider abstraction.
- **WebAdmin**: The web-based frontend for interactive LLM usage, configuration, and administration. Communicates with this API.
- **ConduitLLM.Core**: Shared logic, models, and interfaces for LLM operations, used by both Http and WebAdmin.
- **ConduitLLM.Configuration**: Handles configuration persistence (database, environment, files) and settings management.
- **ConduitLLM.Providers**: Implements provider-specific logic for different LLM backends.
- **ConduitLLM.Tests**: Unit and integration tests for all components.

## Key Features

- **OpenAI-compatible REST API** (`/v1/chat/completions`)
- **Multiple LLM Provider Support** (OpenAI, Anthropic, Azure, Gemini, Cohere, etc.)
- **Model Routing**: Map model names to provider/model pairs
- **API Key & Virtual Key Management**: Supports both provider keys and Conduit-managed virtual keys
- **Streaming and non-streaming responses**
- **Swagger/OpenAPI documentation** (enabled in development)
- **Database-backed and file-based configuration**

## Configuration

### Environment Variables

- `HttpApiHttpPort` (default: 5000): HTTP port for the API
- `HttpApiHttpsPort` (default: 5003): HTTPS port for the API
- `ASPNETCORE_URLS`: Sets the actual listen URLs (usually http://+:80 for Docker)
- Provider API keys can be set via environment variables or `appsettings.json` (see below)

### appsettings.json

- `ConnectionStrings:DefaultConnection`: Path to the configuration database (default: `../configuration.db`)
- `Conduit:ModelProviderMapping`: Maps logical model names to provider/model pairs
- `Conduit:ProviderCredentials`: Stores API keys and endpoints for each provider

Example snippet:
```json
"Conduit": {
  "ModelProviderMapping": {
    "gpt-4-proxy-example": "openai/gpt-4",
    "claude-3-opus-proxy-example": "anthropic/claude-3-opus-20240229"
  },
  "ProviderCredentials": {
    "OpenAI": {
      "ApiKey": "YOUR_OPENAI_API_KEY_OR_USE_ENV_VAR"
    },
    "Anthropic": {
      "ApiKey": "YOUR_ANTHROPIC_API_KEY_OR_USE_ENV_VAR"
    }
    // ... other providers ...
  }
}
```

### Database Configuration

This service supports both Postgres and SQLite, configured via environment variables ONLY (no appsettings.json required):

- **Postgres:**
  - Set `DATABASE_URL` (e.g., `postgresql://user:password@host:5432/database`)
- **SQLite:**
  - Set `CONDUIT_SQLITE_PATH` (e.g., `/data/ConduitConfig.db`)

No other DB-related variables are needed. The service will auto-detect the provider.

### Docker & Port Management

- Designed for containerization. Ports are set via environment variables for flexible deployment.
- When using Docker Compose, ports are configured in the docker-compose.yml file.

## Persistent Database Storage (Docker)

To ensure your SQLite database persists across container restarts, set the `CONDUIT_SQLITE_PATH` environment variable and mount a persistent volume:

```yaml
environment:
  - CONDUIT_SQLITE_PATH=/data/conduit.db
volumes:
  - ./my-data:/data
```

This will store the database at `/data/conduit.db` inside the container, mapped to your host directory `./my-data`.

### Best Practices
- Always use a persistent volume for `/data` in production or testing containers.
- Make sure the volume is writable by the app user (check Docker UID/GID).
- Use `CONDUIT_SQLITE_PATH` for clarity and to avoid accidental data loss.

### Troubleshooting
- **File not created or missing**: Check that the volume is mounted and the path is correct.
- **Permission denied**: Ensure the directory and file are writable by the container user.
- **Database is read-only**: The file or directory may be mounted as read-only or lack write permissions.
- **App uses wrong database file**: Double-check environment variable spelling and container/service environment.

For more help, see the Database Status page in the WebAdmin.

## Running the API

1. **With .NET CLI:**
   ```bash
   export HttpApiHttpPort=5000
   export HttpApiHttpsPort=5003
   dotnet run --project ConduitLLM.Gateway
   ```
2. **With Docker:**
   From the repository root, build the Gateway image:
   ```bash
   docker build -f Services/ConduitLLM.Gateway/Dockerfile -t conduit-http:local .
   ```
   Follow the root [Docker Compose instructions](../../README.md#run-with-docker-compose)
   to configure PostgreSQL, Redis, and the required schema migration before starting the APIs.
3. **With Docker Compose:**
   ```bash
   docker compose up -d
   ```

## API Usage

- Main endpoint: `POST /v1/chat/completions` (OpenAI-compatible)
- Supports both standard and streaming responses
- Requires API key (provider or virtual key) in the `Authorization: Bearer ...` header
- See Swagger UI at `/swagger` (in development mode)

## Virtual Keys

- Virtual keys (`condt_...`) are managed by Conduit and can be used instead of provider keys.
- Spend tracking and access control are supported for virtual keys.

## Refreshing Configuration

- POST `/admin/refresh-configuration` endpoint reloads provider credentials and model mappings from the database.

## Health Checks

The API provides health check endpoints for monitoring and container orchestration:

### Database Health Endpoint

- `GET /health/db`: Checks database connectivity and migration status
- Returns:
  - **200 OK**: Database is reachable and all migrations are applied
  - **503 Service Unavailable**: Database is unreachable or migrations are pending
  - **500 Internal Server Error**: Unexpected error occurred during health check

The response is a JSON payload with the following structure:
```json
{
  "status": "healthy",  // or "unhealthy"
  "timestamp": "2025-04-30T07:30:04.5478Z",
  "details": null  // Contains error information when unhealthy
}
```

### Using with Docker & Kubernetes

For Docker Compose health checks:
```yaml
healthcheck:
  test: ["CMD", "curl", "-f", "http://localhost:5000/health/db"]
  interval: 30s
  timeout: 10s
  retries: 3
  start_period: 40s
```

For Kubernetes readiness and liveness probes:
```yaml
readinessProbe:
  httpGet:
    path: /health/db
    port: 5000
  initialDelaySeconds: 30
  periodSeconds: 10
  timeoutSeconds: 3
  failureThreshold: 3

livenessProbe:
  httpGet:
    path: /health/db
    port: 5000
  initialDelaySeconds: 60
  periodSeconds: 30
  timeoutSeconds: 5
  failureThreshold: 3
```

## Development Notes

- Uses ASP.NET Core Minimal APIs
- Configuration is loaded from both `appsettings.json` and the configuration database
- Provider credentials and model mappings can be managed via the WebAdmin or directly in the database

## Troubleshooting

- When using Docker Compose, use `docker compose down` to stop all services
- Logs are output to the console by default (configurable in `appsettings.json`)
- Use `docker compose logs` to view container logs

## License

See the root of the repository for license information.

