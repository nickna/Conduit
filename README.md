# ConduitLLM

[![CodeQL](https://github.com/nickna/Conduit/actions/workflows/codeql-analysis.yml/badge.svg)](https://github.com/nickna/Conduit/actions/workflows/codeql-analysis.yml)
[![Build & Test](https://github.com/nickna/Conduit/actions/workflows/ci.yml/badge.svg)](https://github.com/nickna/Conduit/actions/workflows/ci.yml)
[![OpenAI Compatible](https://img.shields.io/badge/OpenAI-Compatible-brightgreen.svg)](https://platform.openai.com/docs/api-reference)
[![Built with .NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Docker Ready](https://img.shields.io/badge/Docker-Ready-2496ED)](https://www.docker.com/)

> An LLM gateway: one OpenAI-compatible API in front of many upstream providers, with central
> access control, prepaid billing, and routing and failover.

## Why ConduitLLM?

Are you juggling multiple LLM provider APIs in your applications? ConduitLLM gives you:

- **Single integration point**: Write your code once against an OpenAI-compatible API, switch
  providers anytime
- **Vendor independence**: Avoid lock-in to any single LLM provider
- **Central access control**: Virtual API keys (`condt_...`) with spending limits and prepaid
  balances
- **Routing and failover**: Map one model alias to many providers, spread load across keys, and
  fail over automatically
- **Cost tracking**: Per-request billing against configured model costs, with continuous billing
  reconciliation and a synthetic cost canary

A client authenticates with a **virtual key**, sends a request naming a **model alias**, and
Conduit decides which provider and account actually serves it, calls upstream, streams the result
back, and bills the request. Start with **[Core concepts](docs/concepts.md)** for the full mental
model.

## Key Features

- **OpenAI-compatible REST API** — a drop-in replacement for the OpenAI API; standard OpenAI SDKs
  and tools work without modification
- **Multi-provider, multi-account** — run several providers of the same type side by side
  (e.g. "Production OpenAI" and "Dev Azure"), each with multiple API keys for load spreading and
  failover
- **Model routing** — client-facing model aliases map to concrete provider models; one alias can
  fan out to many providers with session affinity and a kill switch
- **Virtual key management** — Conduit-specific API keys with built-in spend tracking, budgets,
  and prepaid group balances
- **Multi-modal** — chat, embeddings, image generation, video generation, and audio
  (speech-to-text and text-to-speech)
- **Streaming** — real-time SSE token streaming, with optional Conduit-specific events for
  reasoning, tool execution progress, and performance metrics
- **Function calling & tool execution** — server-side function execution with configuration
  management, cost tracking, and agentic mode support
- **Media generation webhooks** — per-request callbacks with retry logic, circuit breakers, and
  delivery tracking for async image/video generation
- **Web-based admin UI** — Next.js dashboard for configuration and monitoring, with real-time
  updates over SignalR
- **Observability built in** — Prometheus metrics, bundled Grafana dashboards and alert rules,
  and multi-layered health checks (see [Monitoring](docs/monitoring.md))
- **Billing correctness** — reserve-then-settle spend admission, continuous reconciliation, and
  alerting (see [Billing correctness alerting](docs/billing-alerting.md))
- **Security features** — IP filtering, rate limiting, failed-auth banning, and security headers

## 🏗️ Architecture

Conduit is two deployable APIs plus an admin UI, backed by a set of shared libraries:

| | Gateway API | Admin API |
|---|---|---|
| Purpose | Serving inference (chat, embeddings, images, audio, …) | Configuring the system (providers, keys, mappings, costs, limits) |
| Who calls it | Your applications and end users | Operators, and the WebAdmin UI |
| Key | A **virtual key** | The **master key** |
| Default port | 5000 | 5002 |

```mermaid
flowchart LR
    Client["Client App"] -->|virtual key| Gateway["ConduitLLM.Gateway(OpenAI-compatible API)"]
    WebAdmin["WebAdmin(Next.js admin UI)"] -->|ephemeral keys| AdminAPI["ConduitLLM.Admin(Admin API)"]
    WebAdmin -->|ephemeral keys| Gateway
    Gateway --> Core["ConduitLLM.Core(Orchestration & routing)"]
    Core --> Providers["ConduitLLM.Providers(Provider adapters)"]
    Providers --> LLM["LLM Backends(OpenAI, Anthropic, etc.)"]
    Gateway --> Config["ConduitLLM.Configuration(Entities, DTOs, messaging)"]
    AdminAPI --> Config
    Core --> Config
```

### Repository layout

- **`Services/ConduitLLM.Gateway`** — the OpenAI-compatible REST API gateway (authentication,
  routing, billing, streaming)
- **`Services/ConduitLLM.Admin`** — the administrative API (providers, keys, mappings, costs,
  media lifecycle)
- **`WebAdmin/`** — the Next.js admin dashboard. Administrators sign in via Clerk; the browser
  then talks to the Gateway and Admin APIs directly using short-lived ephemeral keys, so nothing
  else holds the master key
- **`Shared/ConduitLLM.Core`** — orchestration logic, interfaces, and routing strategies
- **`Shared/ConduitLLM.Providers`** — provider-specific adapters for the upstream LLM services
- **`Shared/ConduitLLM.Configuration`** — entities, DTOs, and the messaging abstraction
- **`Shared/ConduitLLM.Functions`** — server-side function calling and tool execution
- **`Shared/ConduitLLM.Security`** — IP filtering, rate limiting, and related middleware
- **`Tests/`**, **`docs/`**, **`grafana/`**, **`nginx/`**, **`scripts/`** — tests, documentation,
  bundled observability stack, edge proxy config, and dev tooling

### Infrastructure

Conduit needs exactly two backing services:

- **PostgreSQL** (required) — configuration, billing, and the message queue. Async messaging runs
  on **Wolverine over the PostgreSQL transport with an outbox**; there is no separate message
  broker to deploy or operate.
- **Redis** (required for the Gateway, including single-node deployments) — caching, rate
  limiting, spending coordination, and the SignalR backplane for real-time updates. Admin can run
  without Redis; Redis-backed provider-error monitoring is then unavailable.

The bundled Docker Compose stack also runs Prometheus, Grafana (pre-provisioned dashboards and
alert rules), and an nginx edge proxy that serves the WebAdmin and its embedded Grafana UI from a
single origin.

## Quick Start

### Prerequisites

- Docker Desktop (or a compatible engine) for the containerized stack
- .NET 10.0 SDK if you want to build or run the services directly

### Run with Docker Compose

1. **Clone the repository**
   ```bash
   git clone https://github.com/nickna/Conduit.git
   cd Conduit
   ```

2. **Configure the environment**
   - Copy `.env.example` to `.env`.
   - At minimum, set `CONDUIT_GRAFANA_ADMIN_PASSWORD` to a long random value and
     `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` to a strong secret.
   - For production WebAdmin access, configure Clerk keys
     (`NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY`, `CLERK_SECRET_KEY`).
   - Provider API keys are configured later through the WebAdmin/Admin API, not environment
     variables.

3. **Start the stack**
   ```bash
   docker compose up -d
   ```
   Compose runs the one-shot `migrate` service first. Gateway and Admin start only
   after it exits successfully; neither API changes the schema during startup.

4. **Access ConduitLLM**
   - **Gateway API**: `http://localhost:5000` (interactive docs at `/scalar/v1`)
   - **Admin API**: `http://localhost:5002` (interactive docs at `/scalar/v1`)
   - **WebAdmin**: `http://localhost:3000` (embedded Grafana at `/grafana/`)

Production deployments should terminate HTTPS in front of the stack and set
`CONDUIT_API_BASE_URL` / `CONDUIT_GRAFANA_ROOT_URL` to the public-facing URLs.

### Development environment

For day-to-day development use the dev script, which runs the WebAdmin with hot reloading:

```powershell
./scripts/dev.ps1
```

### Docker images

Each service is published as an independent image on GHCR, tagged per
[release channel](docs/Versioning.md) (`:latest` for stable, `:beta` for pre-releases, plus
immutable version tags):

```bash
docker pull ghcr.io/nickna/conduit-webadmin:latest
docker pull ghcr.io/nickna/conduit-admin:latest
docker pull ghcr.io/nickna/conduit-http:latest
```

The checked-in [`docker-compose.yml`](docker-compose.yml) is the reference for wiring them
together (environment variables, health checks, and the observability stack).

## Configuration

Configuration lives in two places: **deploy-time environment variables** (connection strings,
secrets, service URLs) and the **runtime Admin UI/API** (providers, keys, model mappings, costs).
[`.env.example`](.env.example) is the annotated source of truth for every environment variable;
[Configuration](docs/configuration.md) explains the shape of each area. The essentials:

```bash
# PostgreSQL (required) — the app fails to start without it
DATABASE_URL=postgresql://conduit:conduitpass@postgres:5432/conduitdb

# Redis (required for the Gateway) — also enables distributed caching automatically
REDIS_URL=redis://redis:6379

# Backend service-to-service authentication (WebAdmin backend → APIs)
CONDUIT_API_TO_API_BACKEND_AUTH_KEY=your-secure-backend-key

# WebAdmin admin sign-in (Clerk; users need siteadmin: true in public metadata)
NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY=pk_...
CLERK_SECRET_KEY=sk_...
```

Authentication is layered:

- **Virtual keys** (`condt_...`) — what your applications use against the Gateway API; created
  via the Admin API or WebAdmin
- **Master key** — operator access to the Admin API
- **Clerk** — human administrators sign in to the WebAdmin; the browser then uses short-lived
  ephemeral keys, never the master key
- **`CONDUIT_API_TO_API_BACKEND_AUTH_KEY`** — backend service-to-service calls only

## Usage

### Using the API

```bash
# Example: Chat completion request
curl http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer condt_yourvirtualkey" \
  -d '{
    "model": "my-gpt4",
    "messages": [{"role": "user", "content": "Hello, world!"}]
  }'
```

### Using with OpenAI SDKs

Conduit is compatible with standard OpenAI SDKs — point them at your Conduit instance:

```python
from openai import OpenAI

client = OpenAI(
    api_key="condt_yourvirtualkey",
    base_url="http://localhost:5000/v1",  # or your deployed CONDUIT_API_BASE_URL
)

response = client.chat.completions.create(
    model="my-gpt4",
    messages=[{"role": "user", "content": "Hello, world!"}]
)
```

### Integrating beyond OpenAI compatibility

Both APIs ship authoritative OpenAPI contracts — generate a typed client in your language of
choice, or explore them interactively at `/scalar/v1` on each service:

- Gateway API: [`Services/ConduitLLM.Gateway/openapi-gateway.json`](Services/ConduitLLM.Gateway/openapi-gateway.json)
- Admin API: [`Services/ConduitLLM.Admin/openapi-admin.json`](Services/ConduitLLM.Admin/openapi-admin.json)

Conduit-specific streaming events (reasoning, tool execution progress, metrics) are opt-in
extensions documented in the Gateway contract; standard OpenAI clients are unaffected.

## Documentation

The **[documentation](docs/README.md)** is deliberately small — durable reference that covers the
system end to end:

| Doc | What it covers |
|-----|----------------|
| **[Core concepts](docs/concepts.md)** | The mental model: the objects Conduit is built from and what happens when a request comes in. Read this first. |
| **[Configuration](docs/configuration.md)** | Where settings live (deploy-time env vs. runtime Admin UI) and the shape of each area |
| **[Model routing](docs/routing.md)** | How a provider is chosen; failover, session affinity, and the kill switch |
| **[Monitoring](docs/monitoring.md)** | Health checks, metrics, real-time streams, and alerting |
| **[Billing correctness alerting](docs/billing-alerting.md)** | The cost canary, alert rules, and incident response |
| **[Versioning](docs/Versioning.md)** | Semantic versioning, release channels, and version checking |

## 📦 Versioning & Releases

Conduit uses Semantic Versioning, with two release channels cut from `master` via git tags:

- **Stable** (`v3.0.0`) → Docker `:3.0.0` + `:latest`
- **Beta** (`v3.0.0-beta.1`, or any pre-release suffix) → Docker `:3.0.0-beta.1` + `:beta`; a
  beta never moves `:latest`

The WebAdmin displays the current version and can check for updates automatically. See the
[Versioning Guide](docs/Versioning.md) for details.

## Contributing

Contributions are welcome! Development happens on the `dev` branch — please target pull requests
there. See [CLAUDE.md](CLAUDE.md) for development environment setup, build verification, and code
style conventions.

## License

This project is licensed under the terms specified in the `LICENSE` file.
