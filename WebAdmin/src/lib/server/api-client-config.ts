import { ConduitAdminClient } from '@/lib/admin-api';
import { ConduitGatewayClient } from '@/lib/gateway-api';

const isLoopbackUrl = (value: string): boolean => {
  try {
    const hostname = new URL(value).hostname;
    return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '::1';
  } catch {
    return false;
  }
};

/**
 * Validate the environment used by server-side API clients.
 */
export function validateApiClientEnvironment(): void {
  const errors: string[] = [];

  if (!process.env.CONDUIT_API_TO_API_BACKEND_AUTH_KEY) {
    errors.push('Missing required environment variable: CONDUIT_API_TO_API_BACKEND_AUTH_KEY');
  }

  if (process.env.NODE_ENV === 'production') {
    const adminBaseUrl = process.env.CONDUIT_ADMIN_API_BASE_URL ?? 'http://localhost:5002';
    const coreBaseUrl = process.env.CONDUIT_API_BASE_URL ?? 'http://localhost:5000';

    if (isLoopbackUrl(adminBaseUrl)) {
      errors.push('CONDUIT_ADMIN_API_BASE_URL must not use a loopback host in production');
    }
    if (isLoopbackUrl(coreBaseUrl)) {
      errors.push('CONDUIT_API_BASE_URL must not use a loopback host in production');
    }
  }

  if (errors.length > 0) {
    throw new Error(`Environment validation failed:\n${errors.join('\n')}`);
  }
}

// Centralized configuration - lazy evaluation
export const API_CLIENT_CONFIG = {
  // Master key for backend communication
  get masterKey() { 
    return process.env.CONDUIT_API_TO_API_BACKEND_AUTH_KEY ?? '';
  },
  
  // Base URLs
  get adminBaseURL() {
    return process.env.CONDUIT_ADMIN_API_BASE_URL ?? 'http://localhost:5002';
  },
    
  get coreBaseURL() {
    return process.env.CONDUIT_API_BASE_URL ?? 'http://localhost:5000';
  },
  
  // Common settings
  timeout: 60000, // Increased from 30s to 60s for better reliability in docker environment
  maxRetries: 3,
} as const;

// Singleton instances
let adminClient: ConduitAdminClient | null = null;
let gatewayClient: InstanceType<typeof ConduitGatewayClient> | null = null;
let webAdminVirtualKey: string | null = null;

export function getServerAdminClient(): ConduitAdminClient {
  if (!adminClient) {
    // Validate environment at runtime
    validateApiClientEnvironment();
    
    adminClient = new ConduitAdminClient({
      baseUrl: API_CLIENT_CONFIG.adminBaseURL,
      masterKey: API_CLIENT_CONFIG.masterKey,
      timeout: API_CLIENT_CONFIG.timeout,
      retries: API_CLIENT_CONFIG.maxRetries,
    });
  }
  return adminClient;
}

export async function getServerGatewayClient(): Promise<InstanceType<typeof ConduitGatewayClient>> {
  if (!gatewayClient || !webAdminVirtualKey) {
    // Validate environment at runtime
    validateApiClientEnvironment();

    // Get the WebAdmin's virtual key - this will auto-create it with $1000 if it doesn't exist
    if (!webAdminVirtualKey) {
      try {
        console.warn('[API] Getting or creating WebAdmin virtual key...');
        const adminClient = getServerAdminClient();
        // Use the SystemService's getWebAdminVirtualKey method which auto-creates with $1000
        webAdminVirtualKey = await adminClient.system.getWebAdminVirtualKey();
        console.warn('[API] WebAdmin virtual key obtained successfully');
      } catch (error) {
        console.error('[API] Failed to get or create WebAdmin virtual key:', error);
        throw new Error('Failed to retrieve or create WebAdmin virtual key. Ensure the database is accessible and the Admin API is running.');
      }
    }

    gatewayClient = new ConduitGatewayClient({
      apiKey: webAdminVirtualKey,
      baseURL: API_CLIENT_CONFIG.coreBaseURL,
    });
  }
  return gatewayClient;
}
