# Virtual Key Management

## Overview

WebAdmin uses short-lived ephemeral virtual keys for direct browser-to-Gateway requests. The
long-lived WebAdmin virtual key stays behind the Next.js server boundary.

## Flow

1. Browser code calls `getBrowserGatewayClient()`.
2. `ephemeralKeyClient` requests `/api/auth/ephemeral-key` when no unexpired key is cached.
3. The server retrieves or provisions the WebAdmin virtual key through the local Admin API boundary.
4. The server asks the Gateway to mint a short-lived key for that virtual key.
5. The browser's local Gateway client sends the ephemeral key as an opaque Bearer token.

The browser cache refreshes a key 30 seconds before expiration. Clearing the browser client also
clears the ephemeral-key cache. A direct request that receives `401` obtains one fresh key and
retries once.

## Usage

Components and hooks should use the WebAdmin-owned Gateway boundary:

```typescript
import { getBrowserGatewayClient } from '@/lib/client/browserGatewayClient';

const client = await getBrowserGatewayClient();
const models = await client.discovery.getModels();
```

Do not import a published Gateway/Common package or read the SDK workspace from WebAdmin code.

## Security properties

- The persistent WebAdmin virtual key is handled server-side.
- Browser-visible keys are short-lived and limited to Gateway operations.
- Gateway authentication uses `Authorization: Bearer <opaque-key>`.
- Admin operations use a separate ephemeral master-key flow and `X-Master-Key`.
- Browser-visible credentials should still be treated as sensitive and never logged.

See [Gateway API Boundary](./GATEWAY_API_BOUNDARY.md) and
[Admin API Boundary](./ADMIN_API_BOUNDARY.md) for maintenance rules.
