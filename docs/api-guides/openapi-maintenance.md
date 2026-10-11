# API contracts and documentation

Conduit publishes two OpenAPI 3.1.1 contracts:

| API | Authoritative contract | Authentication |
| --- | --- | --- |
| Admin | [openapi-admin.json](../../Services/ConduitLLM.Admin/openapi-admin.json) | `X-Master-Key` header |
| Gateway | [openapi-gateway.json](../../Services/ConduitLLM.Gateway/openapi-gateway.json) | `Authorization: Bearer <virtual-key>` |

The contracts describe endpoint definitions and middleware behavior. They also generate WebAdmin's TypeScript clients. Change endpoint metadata, DTO documentation, or OpenAPI transformers, then regenerate the JSON and TypeScript together. Editing generated files alone fails the CI drift check.

## Using the API

Replace the deployment URLs and credentials below with your own. List configured model aliases before making a billable request. The model listing includes enabled mappings; access to a model is checked when you make a billable request.

```bash
curl 'http://localhost:5000/v1/models' \
  -H 'Authorization: Bearer YOUR_VIRTUAL_KEY'

curl 'http://localhost:5000/v1/chat/completions' \
  -H 'Authorization: Bearer YOUR_VIRTUAL_KEY' \
  -H 'Content-Type: application/json' \
  -d '{"model":"YOUR_MODEL_ALIAS","messages":[{"role":"user","content":"Hello"}]}'

curl 'http://localhost:5001/v1/admin/virtual-keys' \
  -H 'X-Master-Key: YOUR_MASTER_KEY'
```

For chat or Responses streaming, set `stream` to `true` and consume `text/event-stream` rather than a single JSON response. Asynchronous image/video generation returns a task identifier; poll its status to obtain the result. Required balance and provider/model capabilities are separate from authentication.

All published Admin operations require a master key, including routes whose endpoint metadata says `AllowAnonymous`: the Admin security middleware still validates the key. Gateway media GET/HEAD routes permit anonymous retrieval by storage key, but IP bans, filtering, and rate limits can still deny access.

Admin endpoint and exception errors use `application/problem+json` with `AdminProblemDetails`. Security middleware uses `application/json` with a string `error` and numeric `code` for authentication/IP failures. Rate-limit errors use the OpenAI-compatible object `error` envelope. Gateway endpoint errors also use that envelope; IP security failures can use the simple string envelope, and authentication/authorization or parameter binding can return an empty body. Check status and Content-Type before parsing errors. HTTP HEAD responses have no body, including failures.

Use `x-request-id` to correlate failures with server logs. On 429, honor `Retry-After` when present; the documented `X-RateLimit-*` headers describe the applicable window and scope. A 503 can also include `Retry-After`. Headers are optional because they depend on the component that rejected the request.

File imports and media uploads use `multipart/form-data` with a required `file` part. Media uploads additionally accept optional `media_type`. Binary response bodies, SSE streams, and Prometheus scrapes retain their specific content types.

Admin `/metrics` serves a Prometheus/OpenMetrics scrape through middleware. The trailing-slash `/metrics/` route retains the legacy JSON metrics handler. Both require the Admin master key; use the canonical `/metrics` path for monitoring.

In development, each service exposes `/openapi/v1.json` and its Scalar reference at `/scalar/v1`. Production consumers can use the committed contracts without enabling development documentation routes. The relative server `/` resolves against the target deployment; configure your client's base URL explicitly when loading a local contract.

## Maintaining documentation

Every published operation needs a stable unique name, a useful summary, a defined tag, explicit security, and accurate success/failure schemas. Supply descriptions for path/query/header parameters, including units and defaults. Add longer descriptions where streaming, asynchronous completion, side effects, or limitations need explanation.

For Minimal APIs, document the mapped delegate. XML comments on a handler called by a lambda are not automatically copied to that delegate:

```csharp
group.MapGet("/{id}", (int id, WidgetEndpoints endpoints) => endpoints.Get(id))
    .WithName("Widgets_GetById")
    .WithSummary("Get a widget")
    .WithParameterDescription("id", "Identifier of the widget.")
    .Produces<WidgetDto>()
    .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
```

Import `ConduitLLM.Core.OpenApi` for `WithParameterDescription`. The shared transformer transfers metadata without reflection or loading XML files at runtime. Shared transformers document middleware failures, request IDs, and bodyless responses; endpoint definitions own operation-specific statuses and successful response shapes.

```bash
dotnet restore Conduit.slnx
cd tools/openapi
npm ci
npm run generate:offline
npm run validate:openapi
npm run verify:offline
```

Generation does not require running PostgreSQL, Redis, or provider services. `verify:offline` generates both contracts and WebAdmin types twice in isolated directories, checks byte stability, and compares them with committed files. CI runs this verification and rejects drift. Run the focused .NET contract tests from the repository root:

```bash
dotnet test Tests/ConduitLLM.Tests/ConduitLLM.Tests.csproj --filter Component=OpenApi
```

Redocly uses `recommended-strict`, plus contact, parameter-description, tag-definition, and example-validation checks. The wrapper rejects errors, incomplete reports, and **every warning**, even when the CLI exits successfully. There is no warning-debt baseline to rewrite. Saved reports are in `artifacts/openapi-lint`.

## Explicit compatibility exceptions

The manual media-retention cleanup route always returns 501. Its single `operation-2xx-response` lint exception preserves that honest contract. Tests freeze the exact ignored rule/location; do not advertise a fictional successful result. Implementing or removing the stub requires updating its contract, exception, and tests together.

Task cleanup responses retain the established `cleaned_up` and `older_than_hours` names. These are the only semantic casing exceptions. Their removal requires a versioned compatibility decision. Referenced binary upload schemas are valid and do not need media-type exceptions. OpenAI conformance checks remain independently enforced.

The pinned baseline compares the supported chat content-part and function tool-choice unions
recursively, including required members, JSON types, enum restrictions, and image URI format.
Mutation tests guard against arbitrary-object fallbacks and lost discriminator mappings. Other
conformance checks cover a bounded set of fields and types; they do not establish full recursive
equivalence with the entire upstream specification. Provider video, additional audio formats,
and unknown content types are described separately on `/v1/conduit/chat/completions`.
