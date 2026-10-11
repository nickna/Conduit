# Reconciling indeterminate media tasks

An image or video task becomes `indeterminate` when Conduit started a provider
invocation but lost the definitive outcome. Automatic retry is disabled because
the provider may have completed and charged for the original operation.

An unwinding worker releases its unspent customer reservation on this path.
Resolution itself creates no customer debit or refund. A new generation started
by an operator-approved `retry` is billed normally if it succeeds. Recovery of the
original operation uses `completed_no_charge`: Conduit absorbs any known provider
cost and records the disposition. Retrospective billing corrections require a
separate audited, idempotent workflow.

## Detection

Grafana raises **Indeterminate Media Task** when
`media_task_indeterminate_total` increases. The metric's `source` label identifies
whether the task was detected by lease recovery, cancellation, or an exception.

List unresolved tasks with a master key:

```bash
curl -sS "https://CONDUIT_ADMIN/v1/admin/tasks?state=indeterminate&page=1&pageSize=50" \
  -H "Authorization: Bearer MASTER_KEY"
```

The response intentionally excludes task payload and metadata because those fields
may contain a virtual key or customer input.

## Provider reconciliation

For each task:

1. Record the task ID, provider operation ID when present, model, and timestamps.
2. Search the provider dashboard or API for an operation in that time window.
3. Confirm one of these outcomes:
   - the provider did not accept or execute the operation;
   - the provider definitively failed it; or
   - the provider completed it or the outcome is still unknown.
4. Save the provider evidence in the incident record and put a concise reference in
   the resolution `reason`.

If the outcome remains unknown, do not retry; leave it indeterminate and escalate.
If the provider confirms success, recover its original output using the workflow
below. This imports the verified result without invoking generation again.

## Recover confirmed provider success without charge

Verify the provider operation, original provider/model, owning virtual key, and
each output against the incident record. For Replicate image/video, MiniMax video,
and OpenRouter video, Conduit captures the operation ID before polling. The listing
includes the original provider/model and recovery status. Other providers and
historical tasks may lack captured IDs: manually confirm the exact original
operation and ownership with the provider; a timestamp match alone is insufficient.
Conduit imports verified output references rather than submitting another provider
job or guessing provider-specific status URLs.

Obtain public HTTPS URLs for the original media from the provider. Supply one URL
per originally requested image, or one video URL. Downloads must use port 443;
private/local addresses, URL credentials, redirects, empty/malformed media, images
over 20 MiB, and videos over 500 MiB are rejected. Output is validated and copied
through the normal media storage pipeline; an unavailable provider URL is never
returned as a successful fallback. URLs may be signed; treat request bodies and
the private reconciliation metadata as sensitive and do not copy them into logs or tickets.

```bash
curl -sS -X POST "https://CONDUIT_ADMIN/v1/admin/tasks/TASK_ID/resolve" \
  -H "Authorization: Bearer MASTER_KEY" -H "Content-Type: application/json" \
  -d '{
    "resolution": "completed_no_charge",
    "dispatchId": "7c030ad5-7767-474c-842c-f70b74e5948c",
    "virtualKeyId": 42,
    "providerId": 3,
    "model": "original-model-alias",
    "providerModelId": "original-provider-model",
    "providerOperationId": "original-provider-operation",
    "evidence": "Provider incident ABC-123 confirms output ownership and success",
    "reason": "Recover original output; provider cost absorbed by Conduit",
    "resultUrls": ["https://provider.example/original-output.png"],
    "providerCost": 0.75
  }'
```

`providerCost` is optional: omit it when unknown rather than inventing a zero cost.
The stable `dispatchId` is required. Reuse it with exactly the same evidence and
output references after a lost HTTP response; changed evidence with the same ID
returns a conflict. Acceptance atomically reserves the task and commits the
recovery command. Competing recovery, retry, and failure decisions cannot take
the reserved task. The response is `202 Accepted`; the task stays indeterminate
while recovery runs, with `reconciliationOutcome=accepted` in the listing.

Verify that the original task reaches Completed with usable stored media and that
its original webhook arrives. Result, completed audit disposition, media lifecycle
tracking, and terminal notification/webhook intents commit together. Command redelivery and process
restarts cannot repeat generation or create a customer debit/refund. Inspect
`media_task_reconciliations_total{outcome="completed"}` and, when cost is known,
`media_task_provider_cost_written_off_total`, plus outgoing/dead-letter diagnostics.

Failed validation or storage leaves the task indeterminate with a sanitized
`Recovery blocked:` error and `reconciliationOutcome=blocked`. Its reservation is
released so an operator can correct the output references and submit a **new**
dispatch ID. Old blocked commands cannot execute again; earlier decisions remain
in the private audit history. Do not use `retry` for a confirmed provider success.
If the original media is expired or cannot be recovered, close it with
`failed_no_charge`, recording the confirmed provider success, failed delivery,
and deliberate cost write-off in the reason. This means customer delivery failed;
it does not claim the provider failed.

## Retry after confirmed non-execution

Only retry after the provider confirms it did not perform the work:

```bash
curl -sS -X POST \
  "https://CONDUIT_ADMIN/v1/admin/tasks/TASK_ID/resolve" \
  -H "Authorization: Bearer MASTER_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "resolution": "retry",
    "reason": "Provider incident ABC-123 confirms the operation was never accepted"
  }'
```

The API returns `202 Accepted` with a `dispatchId`. Admin publishes a durable
reconciliation command; Gateway idempotently moves the task back to pending and
publishes the stored media request through its transactional handler outbox. A
redelivered command with the same dispatch ID is safe, and duplicate generation
events are rejected by the task claim.

Verify that the task leaves the indeterminate listing and reaches `processing`,
then a terminal state. Also check
`media_task_operator_retries_total{task_type=...}` and logs containing the dispatch
ID.

## Close as failed without charge

When the provider definitively failed and retry is not appropriate, or confirmed
successful media cannot be recovered after investigation:

```bash
curl -i -X POST \
  "https://CONDUIT_ADMIN/v1/admin/tasks/TASK_ID/resolve" \
  -H "Authorization: Bearer MASTER_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "resolution": "failed_no_charge",
    "reason": "Provider incident ABC-123 confirms a terminal failure",
    "providerOperationId": "optional-provider-operation-id"
  }'
```

A successful resolution returns `204 No Content`, marks the task failed and
non-retryable, and publishes the normal task lifecycle update. No customer charge
or refund is created.

## Audit and safety

- All actions require `MasterKeyPolicy` and are recorded by Admin operation and
  audit logging.
- Retry is rejected after the task's retry limit.
- Never put a master key, virtual key, prompt, or provider secret in `reason`.
- Do not update `AsyncTasks` directly; doing so bypasses durable dispatch, cache
  invalidation, lifecycle events, and audit logging.
