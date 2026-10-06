# WebAdmin route handlers

WebAdmin intentionally has a small server-side route surface. As of August 2026 it contains five
handlers under `src/app/api`:

- `auth/ephemeral-key` issues a short-lived Gateway key.
- `auth/ephemeral-master-key` issues a short-lived Admin key.
- `auth/grafana` signs the Grafana request.
- `error-reports` accepts client error reports.
- `health` exposes WebAdmin health.

UI code should call the browser Admin or Gateway clients directly. Add a Next.js route only when an
operation requires server-only credentials, server-side signing, or a WebAdmin-specific health/error
boundary.

## Requirements

- Authenticate before reading a request body or contacting a backend.
- Use `getServerAdminClient` or `getServerGatewayClient`; do not build ad-hoc backend URLs.
- Use the generated Admin/Gateway contracts for backend operations.
- Return `NextResponse.json` with an explicit status for errors.
- Never log keys, authorization headers, or full sensitive request bodies.
- Add a colocated `route.test.ts` for authentication, success, and backend-failure behavior.

See `src/app/api/auth/grafana/route.ts` and `src/app/api/error-reports/route.ts` for tested examples.
