# ConduitLLM.Security

This project contains shared security services, interfaces, and middleware used by both the Gateway API (ConduitLLM.Gateway) and Admin API (ConduitLLM.Admin).

## Overview

The ConduitLLM.Security project provides:
- Security event monitoring and logging
- Threat detection and analysis
- Security metrics collection
- Shared authentication handlers
- Security middleware components
- Rate limiting and IP filtering logic

## Project Structure

```
ConduitLLM.Security/
├── Services/           # Security service implementations
├── Interfaces/         # Security service interfaces
├── Models/            # Internal security models
├── Middleware/        # Security middleware components
└── Extensions/        # Extension methods for security configuration
```

## Key Components

### Services
- `SecurityServiceBase` - Failed-auth tracking, IP bans and rate-limit state shared by
  the Gateway and Admin security services
- `FixedWindowCounter` - Counter backing the fixed-window rate limiters
- `FailedAuthTracker` - Atomic failed-auth attempts and IP bans

Failed-auth state is scoped by API service name and IP address, preserving the
production isolation between Admin and Gateway while sharing state across instances
of the same API. Each failure refreshes the attempt inactivity window. Crossing the
configured threshold creates an absolute-duration ban; later failures update its
count and masked-key metadata without extending the ban. Successful-auth clearing
removes attempts, preserving an active ban. Gateway calls this clear operation after
successful authentication; Admin retains its existing request policy.

With distributed tracking enabled and a registered Redis connection, one Lua
transition records each attempt and creates the ban atomically. Redis time and key
expiry govern the window and ban. State uses the paired hashes
`security:failed_auth:v2:{service:ip}:attempts` and
`security:failed_auth:v2:{service:ip}:ban`. Only masked attempted credentials are
stored. Without Redis, a warning identifies the synchronized memory fallback as
per-instance protection; an arbitrary `IDistributedCache` cannot provide distributed
atomic increments. Disabled distributed tracking does not resolve Redis, and
disabled failed-auth protection performs no tracking or legacy cache operations.

Existing live `ban:{ip}` entries remain visible until their recorded deadline. New
threshold bans are mirrored for legacy readers without renewing that deadline.
Legacy partial attempt counters are not imported into the new atomic protocol;
counting starts a fresh window at cutover while established bans remain enforced.
Drain older writers during rollout, because they still use the former read/write
race. Redis tracking failures are logged; credentials must still pass authentication
even when abuse tracking is unavailable.

### Interfaces
- `ISecurityService` - Request authorization contract

> Security telemetry is exported as Prometheus metrics and structured logs; dashboards
> and alerting live in the Grafana stack rather than in this project.

### Middleware
- Shared security middleware for authentication and authorization
- Rate limiting middleware
- IP filtering middleware

## Usage

Both Gateway API and Admin API reference this project to access shared security functionality. The services are registered via dependency injection and can be used throughout the application.

## Dependencies

- ConduitLLM.Core - Core interfaces and models
- ConduitLLM.Configuration - Security DTOs and configuration
