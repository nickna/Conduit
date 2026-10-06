# Admin contract transport

The migration is complete. Every service in `src/lib/admin-api/services` uses literal operations from
the generated Admin OpenAPI contract through `FetchBaseApiClient.executeContractRead` and
`executeContractOperation`.

The shared executor owns authentication, timeouts, cancellation, retries, callbacks, response parsing,
and error normalization. Services may normalize generated responses into stable UI models, but should
do so in one named adapter at the service boundary. They must not use direct `fetch`, generic URL
helpers, protected-method bracket access, or duplicate generated DTOs.

`npm run check:api-boundary` enforces the transport boundary. When the Admin contract changes:

1. Regenerate `src/generated/admin-api.ts`.
2. Update the affected service and its contract test.
3. Keep compatibility logic only when an active caller requires it; document the caller and remove the
   adapter when that caller is migrated.
