# GEMINI.md

This file provides guidance to Gemini Code Assist when working with code in this repository.

## Table of Contents
1. [⚠️ CRITICAL: Safety First](#️-critical-safety-first)
2. [Quick Start Guide](#quick-start-guide)
3. [Development Environment](#development-environment)
4. [Build & Verification](#build--verification)
5. [Code Quality Standards](#code-quality-standards)
6. [Architecture Essentials](#architecture-essentials)
7. [Documentation Index](#documentation-index)
8. [Repository & Collaboration](#repository--collaboration)

---

# ⚠️ CRITICAL: Safety First

## Commands That WILL Break Development

### ❌ FORBIDDEN WEBADMIN COMMANDS
**These commands break the development container and force a 5+ minute restart:**
- `npm run build` (anywhere in WebAdmin directory)
- `cd WebAdmin && npm run build`
- `./scripts/dev/dev-workflow.ps1 build-webadmin` (production testing only)

**Why?** The development container uses an isolated `.next` directory. Running `npm run build` on the host corrupts the container's build state.

### ✅ SAFE WEBADMIN VERIFICATION
Use these commands instead to verify WebAdmin changes:
- `npm run lint` - Check ESLint errors
- `npm run type-check` - Verify TypeScript types
- Hot reloading automatically validates code changes as you save files.

### ❌ FORBIDDEN DEVELOPMENT COMMANDS
- `docker compose up` for development (always use `./scripts/dev.ps1`)

**If you run forbidden commands, you will:**
1. Break the development environment.
2. Force a restart with `--clean` (which takes 5+ minutes).
3. Waste time and ignore explicit instructions.

---

# Quick Start Guide

## Starting Development Services

**⚠️ CANONICAL DEVELOPMENT STARTUP:**
```bash
./scripts/dev.ps1
```

### Available Flags
```bash
./scripts/dev.ps1              # Standard startup
./scripts/dev.ps1 -WebAdmin    # Rebuild WebAdmin container
./scripts/dev.ps1 -Clean       # Complete reset (removes all volumes)
./scripts/dev.ps1 -Build       # Force rebuild with cache
./scripts/dev.ps1 -Rebuild     # Full rebuild with --no-cache
```

**Flag Details:**
- `-WebAdmin`: Rebuilds the WebAdmin container.
- `-Clean`: Removes containers, volumes, and local development state for a complete reset.
- `-Build`: Rebuilds containers using normal Docker caching.
- `-Rebuild`: Rebuilds containers with `--no-cache`.

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
- ✅ Node modules exist on the HOST, allowing direct `npm` command access.
- ✅ The `WebAdmin` directory is mounted for hot reloading.
- ✅ User ID mapping prevents permission issues by using your host UID/GID.
- ✅ Development containers use `node:22-alpine` directly.
- ✅ Isolated `.next` directories mean the container manages its own build state.

### Development vs Production

| Aspect | Development (`dev.ps1`) | Production (`docker compose up`) |
|--------|------------------------------|----------------------------------|
| WebAdmin Container | `node:22-alpine` with mounted source | Built Next.js app in container |
| Hot Reloading | ✅ Enabled via volume mounts | ❌ Static build |
| User Permissions | Maps to host UID/GID | Runs as container user |
| Node Modules | Shared with host | Container-only |

## Helper Commands

### dev-workflow.ps1
This script simplifies interaction with the development containers.
```bash
./scripts/dev/dev-workflow.ps1 logs                 # View WebAdmin logs in real-time
./scripts/dev/dev-workflow.ps1 shell                # Open a shell inside the WebAdmin container
./scripts/dev/dev-workflow.ps1 lint-fix-webadmin    # Run ESLint with --fix
./scripts/dev/dev-workflow.ps1 exec [command]       # Execute a custom command in the container
```

---

# Build & Verification

## ⚠️ CRITICAL: Always Verify Builds

### WebAdmin Verification (SAFE)
**Never use `npm run build` in development - it breaks the container!**

```bash
cd WebAdmin
npm run lint         # Check for ESLint errors
npm run type-check   # Verify TypeScript types
```

### Backend & SDK Build Commands
```bash
# Full solution build
dotnet build

# Run all tests
dotnet test

# Build individual projects
dotnet build ConduitLLM.Gateway    # Gateway API
dotnet build ConduitLLM.Admin   # Admin API

```

## Incremental Development Rules

1. **NEVER** make more than 3-5 file changes without verifying.
2. **ALWAYS** verify after **ANY** changes:
   - **WebAdmin**: `npm run lint` and `npm run type-check` ONLY.
   - **Backend/SDKs**: Use the appropriate build commands listed above.
3. **Fix ALL errors immediately** to avoid accumulating technical debt.
4. **Never commit code that doesn't verify cleanly.**

---

# Code Quality Standards

## WebAdmin ESLint Strict Rules

The WebAdmin enforces **very strict ESLint rules** that will cause build failures. Pay close attention to these:

### 1. Type Safety Rules
- `@typescript-eslint/no-unsafe-assignment`: Do not assign `any`/`unknown` without explicit type casting.
- `@typescript-eslint/no-unsafe-argument`: Do not pass `any`/`unknown` as function arguments.
- `@typescript-eslint/no-unsafe-member-access`: Do not access properties on `any` types.
- `@typescript-eslint/no-unsafe-return`: Do not return `any` types from functions.

### 2. Console Logging
- ✅ Only `console.warn` and `console.error` are allowed.
- ❌ `console.log` will cause build failures. Use `console.warn` for development debugging.

### 3. Type Casting Pattern
```typescript
// ❌ BAD - will fail ESLint
const data = event.data as MetricsData;

// ✅ GOOD - proper type narrowing
const data = event.data as unknown as MetricsData;

// ✅ BETTER - use a type guard
if (isMetricsData(event.data)) {
  // event.data is now correctly typed within this block
}
```

## C# Code Style Guidelines

### Naming Conventions
- Interfaces: Prefix with 'I' (e.g., `ILLMClient`).
- Async methods: Suffix with 'Async' (e.g., `GetValueAsync`).
- Private fields: Prefix with an underscore (`_logger`).
- Public members: PascalCase.
- Parameters: camelCase.

### Formatting
- Indentation: 4 spaces.
- Braces: Opening braces on a new line (Allman style).

---

# Architecture Essentials

## Database Migrations

**⚠️ CRITICAL: PostgreSQL Syntax ONLY**
- Use the standard EF Core workflow: `dotnet ef migrations add` → `dotnet ef database update`.
- All migration code must use PostgreSQL-compatible syntax (e.g., double quotes for identifiers, `true`/`false` for booleans).

## Provider Architecture

**⚠️ IMPORTANT**: The system supports multiple providers of the same type (e.g., multiple OpenAI configurations). **Provider ID is the canonical identifier, not ProviderType.**
- **Provider ID**: The primary key for `Provider` records. Use this for all lookups and relationships.
- **ProviderType**: An enum that categorizes the provider's API type (e.g., `OpenAI`, `Groq`).

## WebAdmin API Architecture

The WebAdmin backend has a minimal API surface and relies on client-side SDK usage with ephemeral keys.
- **Only 3 API routes exist**: `/api/health`, `/api/auth/ephemeral-key`, and `/api/auth/ephemeral-master-key`.
- When creating new API routes:
    - ✅ Use `getServerAdminClient()` or `await getServerCoreClient()` to interact with backend services.
    - ✅ Wrap all SDK calls in `try/catch` and use `handleSDKError(error)`.
    - ❌ **Never** create SDK clients directly with `new ConduitAdminClient()`.
    - ❌ **Never** expose master keys or secrets to the client.

## Event-Driven Architecture

- The system uses **MassTransit** with RabbitMQ for asynchronous event processing.
- Events are used to ensure cache consistency and decouple services.

## Real-Time Updates

- **SignalR** provides real-time updates via WebSockets, with a Redis backplane for scaling.
- This is used for features like navigation state and media generation progress.

---

# Documentation Index

For more detailed information, refer to the `docs` directory.

## Core Development Guides
- **API Patterns & Best Practices**
- **LLM Client Factory Guide**

## Architecture Documentation
- **Architecture Overview**
- **Provider System**
- **Model & Cost Mapping**
- **Streaming & WebSockets**
- **Async Media Generation**

## Operations & Deployment
- **Operations Documentation**
- **Media Cleanup Configuration** (⚠️ CRITICAL)
- **Deployment Configuration**

## API Integration Guides
- **API Guides Index**
- **SDK Best Practices**

---

# Repository & Collaboration

## My Role as a Collaborator

My goal is to be a thoughtful and effective engineering partner. I will adhere to the following principles:

- **Critical Thinking**: I will analyze requests for edge cases, performance implications, and alignment with existing patterns.
- **Constructive Feedback**: If a request seems suboptimal or unclear, I will ask clarifying questions and may suggest alternatives with clear reasoning.
- **Adherence to Standards**: I will follow the coding standards, architectural patterns, and verification procedures outlined in this document.
- **Incremental Changes**: I will work in small, verifiable steps to maintain code quality and stability.
- **Continuous Learning**: I will use the provided context and documentation to improve my understanding of the project and deliver better results over time.

## Git Branching Rules

- **Protected branch**: `master` - Never push directly.
- **Development branch**: `master` - All development work is based on and merged into this branch.
- **Feature branches**: Create from `master`.
- **Pull requests**: Target the `master` branch.
- **Release process**: Cut releases from `master` using SemVer `v*` tags after that commit's
  master-push `CI required` check succeeds; merging alone does not publish.
- **Retired branch**: `dev` - Do not base work on it, target it with PRs, or synchronize it.

See the [branch retirement policy](docs/operations/dependency-updates.md#activation-and-first-batch-triage)
and [Versioning Guide](docs/Versioning.md#release-channels) for the current development and release workflow.

## Repository Information

- **GitHub Repository**: nickna/Conduit
- **Issues URL**: https://github.com/nickna/Conduit/issues
- **Pull Requests URL**: https://github.com/nickna/Conduit/pulls
