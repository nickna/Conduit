# Monitoring

*Audience: operators running Conduit in production. This doc covers how to tell the system is
healthy and how you find out when it is not. For the settings behind these surfaces, see
[Configuration](./configuration.md).*

Conduit exposes four kinds of operational signal: **health endpoints** for load balancers and
uptime checks, **metrics and traces** for dashboards, **real-time event streams** for live
consoles, and **structured logs** for after-the-fact investigation. Plus a couple of targeted
alerting paths for the two things most worth waking someone for: billing correctness and security.

## Health endpoints

Both the Gateway and Admin APIs expose the standard trio:

- **`/health/live`** — process liveness. It answers "is this instance up and serving?" and stays
  healthy as long as the process can respond. Use it for restart decisions.
- **`/health/ready`** — readiness to take traffic. This is the one a load balancer should gate on:
  it checks that the database schema is current, the messaging bus is reachable, and Redis is
  healthy. Redis is required for the Gateway; Admin checks it when configured.
- **`/health`** — an aggregate view for humans and dashboards.

The distinction matters during deploys: an instance can be *live* (don't kill it) while not yet
*ready* (don't send it traffic) — for example while it waits for a migration to finish.

### Securing the health endpoints

Health endpoints reveal system state, so they are protected. Requests from **private networks**
(the usual `10.x`, `172.16–31.x`, `192.168.x`, and loopback ranges) are always allowed, so
in-cluster probes work with no configuration. **External** callers must present the health key in an
`X-Conduit-Health-Key` header; a request without it receives a **`404`** — Conduit hides the
endpoint's existence rather than returning a `401`. Point external uptime monitors (BetterStack,
Pingdom, and the like) at these endpoints with the key configured.

## Metrics and tracing

Conduit instruments requests, streaming, billing, caching, and more, and exports them via
**OpenTelemetry**. A **Prometheus** scrape endpoint (`/metrics`) is exposed for dashboards — itself
gated to private-network or authenticated callers — and the repo ships Grafana dashboards and alert
rules that read from it. Distributed traces export over OTLP to a target you configure.

> Operator note: metrics are recorded across two instrumentation stacks, and not every metric family
> is guaranteed to reach `/metrics` in every build. After deploying, confirm your Grafana panels
> actually populate before relying on a metric for alerting — don't assume a named metric is scraped.

Every request carries a **correlation ID** (accepted from inbound `X-Correlation-ID` / `X-Request-ID`
/ tracing headers, or generated), stamped onto logs, returned on the response, and **propagated to
upstream provider calls** — so a single ID ties together the whole path of one request across
services.

## Real-time event streams

Conduit pushes live updates over **SignalR** (WebSockets, with automatic fallback to other
transports). The streams fall into two groups by audience:

- **Client-facing streams** carry the progress of a caller's own work — long-running image/video/
  task generation, spend and budget notifications, webhook-delivery outcomes — and are scoped to the
  caller's virtual key, so a client only ever sees its own events.
- **Operator-facing streams** carry system-wide dashboards — live metrics, health and alert state,
  and security events — and require admin authentication.

For horizontal scale, SignalR uses a **Redis backplane** so a client connected to any instance
receives events raised on any other. The Gateway requires Redis even in a single-node deployment;
Admin streams stay local to their instance when Admin runs without Redis.
The exact set of hubs and their events is declared in the Gateway and Admin service code — treat that
as the source of truth rather than a list here, which would drift.

## Provider health

Conduit tracks provider trouble through **error tracking**, not an up/down health probe. Errors from
each provider and key are counted and classified, and when a key (or provider) crosses a fatal
threshold it is **automatically disabled** so routing stops sending traffic to it. Recovery is
manual for invalid credentials and permissions. Keys disabled for insufficient balance enter a
delayed half-open reprobe with exponential backoff and can recover automatically. This error signal
is what [routing](./routing.md#how-candidates-are-scored) reads when it scores mappings down for an
unhealthy provider.

You review and clear provider errors from the **Provider Errors** view in WebAdmin (backed by the
Admin API). Disable and recovery events also create durable admin notifications and best-effort
live announcements. Thresholds, recovery procedures, Redis state, and reprobe troubleshooting are
covered in **[Provider key auto-disable operations](./operations/provider-key-auto-disable.md)**.

If Admin runs without Redis, its provider-error routes return **503 Service Unavailable** with a
problem response explaining that Redis is not configured. Configure Admin with the Gateway's Redis
store to inspect and manage the Gateway's recorded provider errors.

## Alerting: billing correctness and security

Two areas have dedicated alerting beyond the general dashboards:

- **Billing correctness** — a background **cost canary** continuously verifies that every enabled
  model mapping can be priced, and raises alerts on revenue-loss events, unexpected zero-cost
  billing, and canary staleness. This is the highest-value alerting Conduit ships; its setup,
  alert catalog, and incident response live in **[Billing correctness alerting](./billing-alerting.md)**.
- **Client budget alerts** — as a key group's balance is drawn down, Conduit emits budget
  notifications at the **50%, 80%, 90%, and 100%** thresholds over the client spend stream, so
  applications can warn users before requests start returning `402`.

## Security monitoring

The security controls from [Configuration](./configuration.md#deploy-time-configuration-the-environment)
— IP filtering, per-virtual-key rate limiting, and failed-auth banning — are enforced in the request
pipeline and surface violation metrics and an operator security stream. One scale caveat to know:
security-event tracking is held **in memory per instance**, so it resets on restart and each replica
sees only its own traffic. Treat it as a live signal, not a durable audit log; the durable record of
requests lives in the request logs and ledger.

## Streaming in production

Server-Sent Events streaming has its own deployment requirements — proxy buffering must be off,
idle timeouts long enough for the longest stream, and drain/termination handled so in-flight spend
settles. The ingress settings, a validation matrix, and a soak test are in
**[SSE production validation](./sse-production-validation.md)**.

## Where to go next

- **[Configuration](./configuration.md)** — the health key, observability, and security settings.
- **[Core concepts](./concepts.md)** — the request lifecycle these signals observe.
- **[Provider key auto-disable operations](./operations/provider-key-auto-disable.md)** — fatal
  credential/account errors, notifications, and recovery.
- **[Billing correctness alerting](./billing-alerting.md)** · **[SSE production validation](./sse-production-validation.md)** — the two deep operational runbooks.
