import { ConduitGatewayClient } from '@/lib/gateway-api';
import { ephemeralKeyClient } from './ephemeralKeyClient';

let browserClient: InstanceType<typeof ConduitGatewayClient> | null = null;
let cachedKey: string | null = null;

/**
 * Get or create a browser-compatible Gateway API client.
 * Uses ephemeralKeyClient for key management to avoid duplicating
 * the fetch/cache/expiry/retry logic.
 */
export async function getBrowserGatewayClient(): Promise<InstanceType<typeof ConduitGatewayClient>> {
  const { key, gatewayApiUrl } = await ephemeralKeyClient.getKey('discovery-api-access');

  // Recreate client when the key changes (expired and refreshed)
  if (key !== cachedKey) {
    browserClient = null;
    cachedKey = key;
  }

  browserClient ??= new ConduitGatewayClient({
    apiKey: key,
    baseURL: gatewayApiUrl,
  });

  return browserClient;
}

/**
 * Clear the cached browser client.
 * Call this when user logs out or needs to refresh.
 */
export function clearBrowserClient(): void {
  browserClient = null;
  cachedKey = null;
  ephemeralKeyClient.clearCache();
}
