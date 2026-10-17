# Function resources

Function configuration, discovery, and execution use structured JSON throughout their public
contracts. JSON Schema and provider settings are JSON objects, not JSON-encoded strings.

## Canonical execution

Gateway and Admin publish the same `FunctionExecutionDto` lifecycle fields:

```json
{
  "id": "85f88aa3-a3e3-43d3-9e12-f33c4f6dcb36",
  "function_id": 7,
  "status": "completed",
  "input": { "query": "weather" },
  "output": { "answer": "sunny" },
  "error": null,
  "created_at": "2026-07-23T20:00:00Z",
  "started_at": "2026-07-23T20:00:00Z",
  "completed_at": "2026-07-23T20:00:01Z",
  "duration_ms": 1000,
  "cost": {
    "estimated": 0.001,
    "actual": 0.001,
    "currency": "USD",
    "breakdown": {}
  }
}
```

The Gateway renders snake_case. Admin renders the same fields in camelCase. `status` uses the
shared `ExecutionState` string enum. Duration is always an integer number of milliseconds.

Admin responses extend the base schema with an `admin` object containing virtual-key, retry,
lease, webhook, and progress diagnostics. Those operational details are not exposed by Gateway.

## Retrying an execution

`POST /v1/conduit/functions/execute` accepts an optional `Idempotency-Key` header containing
1 to 200 nonblank characters. The key is opaque and case-sensitive, and belongs to the
authenticated virtual key. Another virtual key using the same text creates its own operation
and cannot retrieve the original result. Authentication and key access restrictions still apply
to retries. Omit the header or use distinct keys for independent operations.

The Gateway durably claims the key in PostgreSQL before dispatching the provider. Its fingerprint
includes the function configuration ID, parameters, metadata and provider tool name. JSON object
property order is ignored recursively; array order and scalar values remain significant. Reusing
a scoped key with a different fingerprint returns `409` with `idempotency_conflict` before
another provider call. Changing a configuration does not turn a previously claimed operation
into a new one.

Identical completed retries return the original execution identity, result and cost, including
after a Gateway restart. `Idempotency-Replayed: true` identifies a replay. Settlement uses a
durable execution-specific ledger key; retrying settlement after a crash cannot debit twice.
The original billing group and usage hour are retained. Replays remain readable if that debit
depletes the balance and do not add another request charge.

Concurrent requests across Gateway instances share the claim. While the winner is running,
losers receive `409` with `idempotency_in_progress` and `Retry-After: 3`. Successful responses
and idempotency errors expose `Function-Execution-Id`; errors with an execution identity also
include its `/v1/conduit/functions/executions/{id}` in `Location`. Retry the original POST to
recover the stored result and any pending settlement.

After a claim is accepted, the server continues the operation with its own bounded cancellation
token if the client disconnects. If the provider outcome or final persistence is uncertain,
retries receive `409` with `idempotency_outcome_unknown` instead of invoking the provider again.
A process crash can also leave a claim in progress. Claims and results have no automatic expiry
or reassignment; operator reconciliation is required for abandoned or uncertain claims. A new
key is an independent operation and can repeat external side effects. This protocol cannot
guarantee exactly-once effects at a provider that lacks its own durable idempotency support.

If durable idempotency or settlement is unavailable, the request fails rather than executing
an unprotected retry. A completed result can be retained while settlement is unavailable;
retrying the same key resumes settlement without provider dispatch. Requests without the header
retain their existing inline execution and accounting behavior.

### Provider transport retries and uncertain charges

The Exa and Tavily named HTTP clients automatically retry only GET, HEAD and
OPTIONS requests. Function execution and credential verification use POST and
are not automatically retried by this policy, including after transient HTTP
responses or transport errors. Credential verification still performs its
provider request; its minimal search POST is not assumed free or safe to repeat.
Keyed executions retain their dedicated transport without request retries, and
MCP retains its separate stateful transport.

These adapters do not model or forward a provider idempotency contract. The
Gateway's `Idempotency-Key` protects Conduit claims and settlement; it does not
establish exactly-once remote effects. A provider can accept and charge a request
before its response is lost. An unkeyed Exa/Tavily call can then return a failed
execution while normal configured usage/cost calculation still runs. Its locally
recorded cost is not confirmation of the provider's charge or absence of one.

Reconcile the execution identity, time, parameters and available provider usage
or request records before dispatching another unkeyed call or using a new key.
Uncertain keyed claims require operator reconciliation and remain blocked from
redispatch; repeating an unkeyed call is a new operation. Disabling transport
retries does not supply durable replay protection for unkeyed requests.

## Configuration and discovery

`providerSettings` and `parameterSchema` in Admin configuration requests and responses are
open-ended JSON objects. Gateway function discovery returns `parameter_schema` as the same kind of
object, both in the function list and the per-function parameter endpoint.
