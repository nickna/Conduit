# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

# ⚠️ CRITICAL: Safety First

## Commands That WILL Break Development

### ❌ FORBIDDEN WEBADMIN COMMANDS
**These commands break the development container and force a 5+ minute restart:**
- `npm run build` (anywhere in WebAdmin directory)
- `cd WebAdmin && npm run build`
- `./scripts/dev/dev-workflow.ps1 build-webadmin` (production testing only)

**Why?** The development container uses an isolated `.next` directory. Running npm build on the host corrupts the container's build state.

### ✅ SAFE WEBADMIN VERIFICATION
Use these instead:
- `npm run lint` - Check ESLint errors
- `npm run type-check` - Verify TypeScript types
- Hot reloading automatically validates code changes

### ❌ FORBIDDEN DEVELOPMENT COMMANDS
- `docker compose up` for development (always use `./scripts/dev.ps1`)

**If you run forbidden commands, you will:**
1. Break the development environment
2. Force a restart with `--clean` (5+ minutes)
3. Waste time and ignore explicit instructions

---

# Quick Start Guide

## Starting Development Services

**⚠️ CANONICAL DEVELOPMENT STARTUP:**
```powershell
./scripts/dev.ps1
```

### Available Flags
```powershell
./scripts/dev.ps1              # Standard startup
./scripts/dev.ps1 -WebAdmin    # Rebuild WebAdmin container
./scripts/dev.ps1 -Clean       # Complete reset (removes all volumes)
./scripts/dev.ps1 -Build       # Force rebuild (uses cache where possible)
./scripts/dev.ps1 -Rebuild     # Full rebuild with --no-cache (nuclear option)
./scripts/dev.ps1 -Logs -LogService webadmin  # Show container logs
```

**Flag Details:**
- `-WebAdmin`: Restarts WebAdmin container (fixes Next.js issues)
- `-Clean`: Removes containers, volumes, node_modules, build artifacts
- `-Build`: Rebuilds containers (uses cache where possible)
- `-Rebuild`: Full rebuild with `--no-cache` flag (nuclear option)
- `-Logs [-LogService <name>]`: Show container logs for specific service

## Available Services
After startup, these services are available:
- 🌐 **WebAdmin**: http://localhost:3000 (Next.js with hot reloading)
- 📚 **Gateway API**: http://localhost:5000/scalar/v1
- 🔧 **Admin API**: http://localhost:5002/scalar/v1

## Quick Verification
```bash
docker ps                              # Check running containers
docker compose -f docker-compose.yml -f docker-compose.dev.yml logs -f [service]
```

---

# Development Environment

## How Development Works

### Key Features
- ✅ Node modules exist on HOST - direct npm command access
- ✅ WebAdmin directory mounted for hot reloading
- ✅ User ID mapping prevents permission issues (uses your UID/GID)
- ✅ Development containers use node:22-alpine directly
- ✅ Isolated .next directories - container manages its own build state

### Development vs Production

| Aspect | Development (`dev.ps1`) | Production (`docker compose up`) |
|--------|------------------------------|----------------------------------|
| WebAdmin Container | `node:22-alpine` with mounted source | Built Next.js app in container |
| Hot Reloading | ✅ Enabled via volume mounts | ❌ Static build |
| User Permissions | Maps to host UID/GID | Runs as container user |
| Node Modules | Shared with host | Container-only |

### Technical Implementation

**Volume Mounting:**
- WebAdmin source: `./WebAdmin:/app/WebAdmin`
- Node modules accessible from both host and container

**Permission Handling:**
1. Container starts as root
2. Installs su-exec (Alpine Linux user switching)
3. Fixes ownership to `${DOCKER_USER_ID}:${DOCKER_GROUP_ID}`
4. Switches to mapped user for all operations

**Environment Variables:**
```bash
export DOCKER_USER_ID=$(id -u)
export DOCKER_GROUP_ID=$(id -g)
```

## Helper Commands

### dev-workflow.ps1
```powershell
# Build Commands
./scripts/dev/dev-workflow.ps1 build-webadmin       # Build WebAdmin application

# Lint/Type Commands
./scripts/dev/dev-workflow.ps1 lint-webadmin        # Run ESLint on WebAdmin
./scripts/dev/dev-workflow.ps1 lint-fix-webadmin    # Run ESLint with --fix
./scripts/dev/dev-workflow.ps1 type-check-webadmin  # Run TypeScript type checking

# NPM Commands
./scripts/dev/dev-workflow.ps1 npm-install-webadmin # Install WebAdmin dependencies

# Container Commands
./scripts/dev/dev-workflow.ps1 shell                # Open bash shell in container
./scripts/dev/dev-workflow.ps1 logs                 # Show WebAdmin container logs
./scripts/dev/dev-workflow.ps1 restart-webadmin     # Restart WebAdmin container
./scripts/dev/dev-workflow.ps1 status               # Show container status
./scripts/dev/dev-workflow.ps1 exec <cmd>           # Execute command in container

# Local Development
./scripts/dev/dev-workflow.ps1 install-local        # Install all dependencies locally
./scripts/dev/dev-workflow.ps1 build-local          # Build all TypeScript projects locally
./scripts/dev/dev-workflow.ps1 clean                # Clean node_modules and build artifacts
```

### Other Helper Scripts
- `scripts/dev/fix-webadmin-errors.ps1` - Automated TypeScript/ESLint fixes
  - `-LintOnly` - Run linting and fixing only (skip build)
  - `-BuildOnly` - Run build only (skip linting)
  - `-CheckOnly` - Check environment and permissions only
- `scripts/dev/create-test-virtual-key.ps1` - Create virtual keys for testing
- `scripts/dev/setup-r2-dev.ps1` - Setup Cloudflare R2 development environment
- `scripts/test/validate-eslint.ps1` - Validate ESLint configuration
- `scripts/test/validate-eslint-strict.ps1` - Strict ESLint validation (CI/CD)
- `scripts/migrations/validate-migrations.ps1` - Validate EF Core migrations

## Troubleshooting

### Permission Denied Errors
```powershell
# Symptom: npm EACCES errors, cannot write to node_modules
./scripts/dev.ps1 -Clean
```

### After Adding New Packages
```powershell
./scripts/dev.ps1 -WebAdmin
```

### Container Conflicts
```powershell
# Symptom: Containers already exist or port conflicts
docker compose down --volumes --remove-orphans
./scripts/dev.ps1 -Clean
```

### Next.js Build Issues / Stale Builds
```powershell
./scripts/dev.ps1 -WebAdmin
```

### WebAdmin Not Starting
```powershell
# Check logs
docker compose -f docker-compose.yml -f docker-compose.dev.yml logs webadmin

# Common causes:
# 1. Port 3000 already in use
# 2. Missing environment variables in .env
# 3. Node modules corruption (use -Clean)
```

### Hot Reload Not Working
```powershell
# Verify file mounting
docker compose -f docker-compose.yml -f docker-compose.dev.yml exec webadmin ls -la /app/WebAdmin/

# Clean host build artifacts (container has isolated .next)
Remove-Item -Recurse -Force WebAdmin/.next
./scripts/dev.ps1 -WebAdmin
```

---

# Build & Verification

## ⚠️ CRITICAL: Always Verify Builds

### WebAdmin Verification (SAFE)
**Never use `npm run build` - it breaks the container!**

```bash
cd WebAdmin
npm run lint         # Check ESLint errors
npm run type-check   # Verify TypeScript types
```

### Backend & SDK Build Commands
```bash
# Full solution
dotnet build

# All tests
dotnet test

# Specific test
dotnet test --filter "FullyQualifiedName=ConduitLLM.Tests.TestClassName.TestMethodName"

# Individual projects
dotnet build ConduitLLM.Gateway    # Gateway API
dotnet build ConduitLLM.Admin   # Admin API

```

## Incremental Development Rules

1. **NEVER make more than 3-5 file changes without verifying**
2. **ALWAYS verify after ANY changes:**
   - WebAdmin: `npm run lint` and `npm run type-check` ONLY
   - Backend: Use build commands above
3. **Fix ALL errors immediately** - do not accumulate technical debt
4. **Never commit code that doesn't verify cleanly**

## Development Workflow

1. Make changes to code
2. Verify immediately:
   - **WebAdmin**: `npm run lint` && `npm run type-check`
   - **Backend**: `dotnet build`
3. Test changes:
   - API changes: Test with Swagger UI or curl
   - UI changes: Verify in browser with dev tools open
4. Clean up temporary files/scripts
5. Commit only verified code

---

# Code Quality Standards

## WebAdmin ESLint Strict Rules

The WebAdmin uses **very strict ESLint rules** that cause build failures:

### 1. Type Safety Rules
- `@typescript-eslint/no-unsafe-assignment` - Cannot assign `any`/`unknown` without explicit casting
- `@typescript-eslint/no-unsafe-argument` - Cannot pass `any`/`unknown` as arguments
- `@typescript-eslint/no-unsafe-member-access` - Cannot access properties on `any` types
- `@typescript-eslint/no-unsafe-return` - Cannot return `any` types

### 2. Console Logging
- ✅ Only `console.warn` and `console.error` allowed
- ❌ `console.log` causes build failures
- Use `console.warn` for development debugging

### 3. Nullish Coalescing
- Use `??` instead of `||` for default values
- Rule: `@typescript-eslint/prefer-nullish-coalescing`

### 4. Type Casting Pattern
```typescript
// ❌ BAD - will fail ESLint
const data = event.data as MetricsData;

// ✅ GOOD - proper type narrowing
const data = event.data as unknown as MetricsData;

// ✅ BETTER - type guard
if (isMetricsData(event.data)) {
  // event.data is now typed
}
```

## TypeScript/React Best Practices

- Replace `any` types incrementally, verify immediately
- Check existing error handling patterns before creating new ones
- Use small changes (1-3 files at a time)
- Follow established import patterns
- Run `npm run lint` frequently during development
- Address warnings immediately

## C# Code Style Guidelines

### Naming Conventions
- Interfaces: Prefix with 'I' (e.g., `ILLMClient`)
- Async methods: Suffix with 'Async'
- Private fields: Prefix with underscore (`_logger`)
- Public members: PascalCase
- Parameters: camelCase

### Formatting
- Indentation: 4 spaces
- Braces: Opening braces on new line (Allman style)
- Max line length: ~100 characters

### Error Handling
- Use custom exception types inheriting from base exceptions
- Include contextual information in exception messages
- Use try/catch with appropriate logging

### Testing
- Test method pattern: `MethodName_Condition_ExpectedResult`
- One assertion per test (preferred)
- Use Moq for mocking dependencies

---

# Architecture Essentials

## Database Migrations

**⚠️ CRITICAL: PostgreSQL Syntax ONLY**

### Requirements
- Use standard EF Core workflow: `dotnet ef migrations add` → `dotnet ef database update`
- PostgreSQL syntax only (double quotes for identifiers, `true`/`false` booleans)
- Test tooling first: `dotnet ef --version`
- **Expand/contract policy**: migrations must be backward-compatible with the previous
  release's code; destructive steps (drop/rename) ship one release later, after no
  deployed code references the old shape
- Normal services never apply schema changes. The explicit `migrate` CLI verb is
  the release gate; runtime `CONDUIT_MIGRATION_MODE` supports `Wait` (default) or
  `Skip` — see
  [`migration-deployment-strategy.md`](docs/operations/deployment/migration-deployment-strategy.md)
- Repository interfaces and implementations live in
  `Shared/ConduitLLM.Configuration/Interfaces/` and
  `Shared/ConduitLLM.Configuration/Repositories/`; follow the neighboring patterns

### Common Mistakes
- ❌ SQL Server syntax: `IsActive = 1`
- ✅ PostgreSQL syntax: `"IsActive" = true`
- ❌ Single quotes for identifiers
- ✅ Double quotes for identifiers

## Provider Architecture

**⚠️ IMPORTANT**: The codebase supports multiple providers of the same type (e.g., multiple OpenAI configurations). **Provider ID is the canonical identifier, not ProviderType.**

### Key Concepts
- **Provider ID**: Canonical identifier for Provider records (use for lookups, relationships, identification)
- **ProviderType**: Categorizes by API type (OpenAI, Anthropic, etc.) - multiple providers can share the same type
- **Provider Name**: User-facing display name (can change, don't use for identification)
- **ProviderKeyCredential**: Individual API keys per provider (supports load balancing/failover)

### Architecture
- **Provider Entity**: Instance (e.g., "Production OpenAI", "Dev Azure OpenAI")
- **ProviderKeyCredential Entity**: API keys with ProviderAccountGroup for account separation
- **ModelProviderMapping**: Links model aliases to Provider.Id (NOT ProviderType!)
- **ModelCost**: Flexible cost configs via ModelCostMapping
- **Provider key resilience**: Fatal credential/account errors disable keys, not healthy sibling
  capacity. See [ADR 0003](docs/decisions/0003-provider-key-auto-disable-policy.md) and the
  [operator runbook](docs/operations/provider-key-auto-disable.md).

> Provider types live in the `ProviderType` enum (`Shared/ConduitLLM.Configuration`) — read it for the current set.

**References:**
- [`docs/concepts.md`](docs/concepts.md) - Provider and model mental model
- [`docs/routing.md`](docs/routing.md) - Provider selection, failover, and session affinity
- `Shared/ConduitLLM.Configuration/ProviderService.cs` and
  `Shared/ConduitLLM.Providers/DatabaseAwareLLMClientFactory.cs` - Current implementation

## Security & Authentication

### WebAdmin Authentication
- **Human admins**: Authenticate through Clerk (not password-based)
- **Backend service-to-service**: Uses `CONDUIT_API_TO_API_BACKEND_AUTH_KEY`

### Backend Authentication Key
**CONDUIT_API_TO_API_BACKEND_AUTH_KEY**:
- Used by WebAdmin backend to authenticate with Gateway API and Admin API
- Server-to-server communication only
- NOT for end-users or client applications
- Configured on WebAdmin service

### Health Monitoring Key
**CONDUIT_HEALTH_MONITORING_KEY**:
- Used by external monitoring services (BetterStack, Pingdom, etc.) to access health endpoints
- Passed via `X-Conduit-Health-Key` header
- Private network requests (10.x, 172.16-31.x, 192.168.x, 127.x) don't require this key
- External requests without valid key receive `404 Not Found`
- See [`docs/monitoring.md`](docs/monitoring.md#securing-the-health-endpoints) for
  configuration details

## WebAdmin API Architecture

**The WebAdmin has only 3 API routes** - relies on client-side SDK usage with ephemeral keys:
- `/api/health` - Health check endpoint
- `/api/auth/ephemeral-key` - Generate virtual keys for Gateway API access
- `/api/auth/ephemeral-master-key` - Generate master keys for Admin API access

### When Writing New API Routes
- ✅ Use `getServerAdminClient()` for Admin API access
- ✅ Use `await getServerCoreClient()` for Gateway API access (async!)
- ✅ Wrap all SDK calls in `try/catch` with `handleSDKError(error)`
- ✅ Return `NextResponse.json(data)` for responses
- ✅ Validate request input before passing to SDK
- ❌ **Never** create SDK clients directly with `new ConduitAdminClient()`
- ❌ **Never** expose master keys to clients

**Source of truth:** `WebAdmin/src/lib/server/api-client-config.ts` and the route examples
under `WebAdmin/src/app/api/`

## Media Storage & Cleanup

**⚠️ CRITICAL**: Proper cleanup configuration required to prevent unbounded storage costs

### Configuration
- Development: S3-compatible storage (configure in .env)
- Production: AWS S3 or Cloudflare R2
- **MUST** configure storage provider in Admin API for automatic cleanup
- See `docs/operations/deployment/media-cleanup-configuration.md`

### Cloudflare R2 Specifics
- Automatic detection based on service URL
- 10MB multipart upload chunks (vs 5MB default)
- May require manual CORS setup in dashboard
- Enable public access in R2 dashboard for CDN
- Free egress bandwidth

## Event-Driven Architecture

- **Publish/consume through the Conduit-owned `IEventBus` / `IEventHandler<T>` abstraction**
  (`ConduitLLM.Configuration.Messaging`), NOT transport types directly. Inject `IEventBus`
  to publish; implement `IEventHandler<TEvent>` (handler context is `IEventContext`) to consume.
- The abstraction runs on **Wolverine over the PostgreSQL transport + outbox** in both dev and
  production — it reuses `DATABASE_URL`; there is no separate message broker to run. The former
  MassTransit/RabbitMQ backend was removed after the Wolverine cutover (epic #909); selecting
  `MassTransit` fails boot with a pointer to Wolverine (`MessagingBackend.cs`).
- Events ensure cache consistency and eliminate race conditions; spend ordering uses a
  strict-ordering listener (one node, sequential handling), webhook retry is scheduled delivery.
- The 4 tuned endpoints are described as data in `ConduitEndpointPolicies` (retry,
  circuit-breaker, rate-limit, ordering), translated to Wolverine by `WolverineEndpointPolicy`.

**See:** `docs/configuration.md` (messaging section) and
`Shared/ConduitLLM.Configuration/Messaging/`

## Real-Time Updates

- **SignalR** provides real-time updates via WebSockets
- Redis backplane for horizontal scaling
- Falls back to polling if WebSocket fails
- Specialized hubs for content-generation, task-tracking, spend-notifications, virtual-key-management, webhook-delivery, and others
- Observability hubs (metrics, health-monitoring, security-monitoring, usage-analytics) were removed — Prometheus/Grafana owns that surface
- Hub source: `Services/ConduitLLM.Gateway/Hubs/`

**See:** [`docs/monitoring.md`](docs/monitoring.md#real-time-event-streams) and
[`docs/configuration.md`](docs/configuration.md#reliable-signalr-queue-delivery)

## High-Throughput Configuration

- Messaging throughput is tuned per endpoint via `ConduitEndpointPolicies` (concurrency
  limits, partition-key ordering, retries) — queues live in PostgreSQL, no broker to scale
- HTTP client pooling: 50 connections per server
- Circuit breakers and rate limiting

---

# Documentation Index

The documentation starts at **[docs/README.md](docs/README.md)**. Current entry points:

| Topic | Start Here |
|-------|------------|
| Core concepts | [docs/concepts.md](docs/concepts.md) — APIs, core objects, request and spend flow |
| Configuration | [docs/configuration.md](docs/configuration.md) — Deploy-time and runtime settings |
| Model routing | [docs/routing.md](docs/routing.md) — Selection, failover, affinity, and controls |
| Monitoring | [docs/monitoring.md](docs/monitoring.md) — Health, metrics, streams, and alerts |
| API route maps | [Gateway namespaces](docs/api-guides/gateway-route-namespaces.md) and [Admin routes](docs/api-guides/admin-api-routing-map.md) |
| Operations | [Provider keys](docs/operations/provider-key-auto-disable.md), [rate limiting](docs/operations/rate-limiting.md), and [messaging](docs/operations/messaging-throughput.md) |
| Versioning | [docs/Versioning.md](docs/Versioning.md) — Release and package version scheme |

### Frequently Referenced Docs
- **[Core Concepts](docs/concepts.md)** — Provider, model, key, mapping, and request flow
- **[Configuration](docs/configuration.md)** — Environment and Admin-managed settings
- **[Model Routing](docs/routing.md)** — Candidate selection and failover
- **[Monitoring](docs/monitoring.md)** — Health endpoints, metrics, streams, and alerting
- **[Media Cleanup](docs/operations/deployment/media-cleanup-configuration.md)** — S3/R2 cleanup (CRITICAL)
- **[Messaging Throughput](docs/operations/messaging-throughput.md)** — Queue limits and tuning

---

# Repository & Collaboration

## Git Branching Rules

- **Protected branch**: `master` - Never push directly
- **Development branch**: `master` - All development work is based on and merged into this branch
- **Feature branches**: Create from `master`
- **Pull requests**: Target the `master` branch
- **Release process**: Cut releases from `master` using SemVer `v*` tags after that commit's
  master-push `CI required` check succeeds; merging alone does not publish

### Current Branch Status
- Default branch: `master`
- Active development: `master`
- Retired branch: `dev` - Do not base work on it, target it with PRs, or synchronize it

See the [branch retirement policy](docs/operations/dependency-updates.md#activation-and-first-batch-triage)
and [Versioning Guide](docs/Versioning.md#release-channels) for the current development and release workflow.

## Collaboration Guidelines

- **Challenge and question**: Don't immediately agree with suboptimal/unclear requests
- **Push back constructively**: Suggest better alternatives with clear reasoning
- **Think critically**: Consider edge cases, performance, maintainability, best practices
- **Seek clarification**: Ask follow-up questions when requirements are ambiguous
- **Propose improvements**: Suggest better patterns, more robust solutions
- **Be a thoughtful collaborator**: Act as a good teammate improving project quality

## Repository Information

- **GitHub Repository**: nickna/Conduit
- **Issues URL**: https://github.com/nickna/Conduit/issues
- **Pull Requests URL**: https://github.com/nickna/Conduit/pulls
