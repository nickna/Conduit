# Gateway route namespaces

The Gateway reserves direct `/v1/...` routes for resources that follow the
OpenAI API contract. Conduit-specific extensions live below `/v1/conduit/...`.

## OpenAI-compatible routes

- `/v1/models`
- `/v1/chat/completions`
- `/v1/responses` (stateless)
- `/v1/embeddings`
- `/v1/audio/...`
- `/v1/images/generations`

The legacy `/v1/completions` route always returns 501 and is excluded from the
published contract. See [API contracts and documentation](openapi-maintenance.md)
for examples and validation instructions.

## Conduit extension route map

| Previous prefix or route | Canonical route |
|---|---|
| `/v1/auth/...` | `/v1/conduit/auth/...` |
| `/v1/batch/...` | `/v1/conduit/batch/...` |
| `/v1/discovery/...` | `/v1/conduit/discovery/...` |
| `/v1/downloads/...` | `/v1/conduit/downloads/...` |
| `/v1/functions/...` | `/v1/conduit/functions/...` |
| `/v1/images/generations/async` | `/v1/conduit/images/generations/async` |
| `/v1/images/generations/{taskId}` | `/v1/conduit/images/generations/{taskId}` |
| `/v1/images/generations/{taskId}/status` | `/v1/conduit/images/generations/{taskId}/status` |
| `/v1/media/...` | `/v1/conduit/media/...` |
| `/v1/models/{modelId}/metadata` | `/v1/conduit/models/{modelId}/metadata` |
| `/v1/rerank` | `/v1/conduit/rerank` |
| `/v1/tasks/...` | `/v1/conduit/tasks/...` |
| `/v1/videos/...` | `/v1/conduit/videos/...` |

The old extension routes are intentionally not retained as aliases. This keeps
the generated contract unambiguous and prevents new non-OpenAI resources from
leaking into the reserved namespace.

## Error contract

Gateway endpoint and exception-handler errors use the OpenAI error envelope
and carry the request's `x-request-id` header:

```json
{ "error": { "message": "...", "type": "invalid_request_error", "code": "model_not_found", "param": "model" } }
```

IP security middleware can instead return `application/json` with a string
`error` and numeric `code`. Authentication/authorization and parameter binding
can return an empty body. Check status and Content-Type before parsing a failure.
HEAD failures never carry bodies. The OpenAPI contract documents these variants
and the optional rate-limit headers.

Status selection is centralized in `ExceptionToResponseMapper`
(`Shared/ConduitLLM.Core/Exceptions/`), applied by `OpenAIErrorMiddleware`. Endpoint
handlers must **not** translate exceptions to status codes themselves: observe (activity
tags, metrics) and rethrow. A blanket `catch (Exception) → 500` in a handler or an endpoint
filter silently downgrades every client error to a server error.

| Status | `code` | `type` | Raised by |
|---|---|---|---|
| 400 | `invalid_request_body` | `invalid_request_error` | Malformed JSON, or a body missing a required member |
| 400 | `validation_error` | `invalid_request_error` | `ValidationException` (parameter and tool validation) |
| 400 | `invalid_request` | `invalid_request_error` | `InvalidRequestException` (`code` overridable) |
| 401 | `missing_key`, `invalid_key`, `key_disabled`, `key_expired` | `authentication_error` | Virtual-key validation |
| 402 | `insufficient_balance` | `billing_error` | Balance check, or billing admission in Enforce mode |
| 403 | `model_not_allowed` | `permission_error` | Key is not permitted to use the requested model |
| 403 | `forbidden` | `invalid_request_error` | `AuthorizationException` |
| 404 | `model_not_found` | `invalid_request_error` | Unknown model alias, **or** every route for it administratively disabled |
| 408 | `request_timeout` | `timeout_error` | `RequestTimeoutException` |
| 413 | `payload_too_large` | `invalid_request_error` | Body over the endpoint's size limit |
| 429 | `rate_limit_exceeded` | `rate_limit_error` | Virtual-key or provider rate limit (sends `Retry-After`) |
| 500 | `internal_error` | `server_error` | Unexpected failure; the message is redacted outside Development |
| 503 | `service_unavailable` | `service_unavailable` | No **healthy** route: open circuit or no usable provider credential |

`404` versus `503` is the distinction worth remembering: a disabled mapping, provider or
model/provider-type association means the alias is not available to the caller at all, so it
is a `404` — the same answer OpenAI gives for a model you cannot access. `503` is reserved
for routes that are configured and expected to come back, and is therefore the only one of
the two worth retrying.
