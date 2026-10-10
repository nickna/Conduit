# Conduit documentation

ConduitLLM is an **LLM gateway**: one OpenAI-compatible API in front of many upstream providers,
with central access control, prepaid billing, and routing and failover. If you are new here, start
with **[Core concepts](./concepts.md)**.

## The docs

These four cover the system end to end:

- **[Core concepts](./concepts.md)** — the mental model: the objects Conduit is built from and what
  happens when a request comes in. Read this first.
- **[Configuration](./configuration.md)** — where settings live (deploy-time environment vs. runtime
  Admin UI) and the shape of each area.
- **[Model routing](./routing.md)** — how a chat request's provider is chosen, and how failover,
  session affinity, and the kill switch behave.
- **[Multimodal chat](./api-guides/multimodal-chat.md)** — the ordered image, PDF, audio, and video
  request contract, PDF parsing, limits, and capability discovery.
- **[API contracts and documentation](./api-guides/openapi-maintenance.md)** — authoritative contracts,
  authentication, error handling, request examples, and regeneration/validation.
- **[Monitoring](./monitoring.md)** — health checks, metrics, real-time streams, and alerting.

Deeper operational runbooks stand on their own and are linked from Monitoring:

- **[Billing correctness alerting](./billing-alerting.md)** — the cost canary, alert rules, and
  incident response.
- **[Provider key auto-disable](./operations/provider-key-auto-disable.md)** — investigating fatal
  credential/account errors and restoring keys safely.
- **[Rate limiting](./operations/rate-limiting.md)** — what can be limited and at which scope, what
  a throttled client sees, and what happens when the limit store is unreachable.
- **[Messaging throughput](./operations/messaging-throughput.md)** — what caps the event bus, which
  queue binds first, why latency is the wrong alarm, and what to tune before anything drastic.

**[Versioning](./Versioning.md)** documents the release and version scheme.

## What belongs in `docs/` (and what doesn't)

This folder is deliberately small. It holds **durable, steady-state reference** for people who
operate or build against Conduit — and nothing else. That constraint is the point: this directory
has been allowed to fill with throwaway documents and been wiped clean more than once, and the only
thing that keeps it useful is being strict about what lands here.

**A document belongs here only if it:**

- describes how the system *is*, not a change in flight or a moment in time;
- has exactly **one home** — one topic, one file, not the same fact restated in three places;
- explains concepts and **points to the source of truth** for specifics, rather than copying them.

**These do _not_ belong here — and where they go instead:**

| Not this | Put it here |
|---|---|
| Implementation plans, phase/rollout/migration plans | The PR description, or the tracking issue / epic |
| Status, progress, or "summary of work done" reports | The PR or issue it describes |
| Audits, investigations, incident write-ups | The issue, or an incident tracker |
| Point-in-time decision records (ADRs) | A separate, dated ADR log — never mixed into reference docs |
| Enumerations that mirror code (env-var tables, provider lists, hub/event catalogs) | Link to the source (`.env.example`, the Admin UI, the code) |

**The one rule that prevents the rot:** if the code, the Admin UI, or `.env.example` already
declares something, *describe the concept and link to it* — never re-tabulate it here. Copied lists
are what go stale first (the last wipe cleared docs that still named a message broker the system had
already replaced). When in doubt, write less and link more.
