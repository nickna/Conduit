import { NextResponse } from 'next/server';
import createClient from 'openapi-fetch';
import { toApiErrorResponse } from '@/lib/errors/api-errors';
import type { paths as AdminPaths } from '@/generated/admin-api';
import {
  adminEphemeralKeySchema,
  parseCriticalResponse,
} from '@/lib/api-transport/critical-response-validation';
import type { WebAdminEphemeralMasterKeyResponse } from '@/lib/api-transport/contracts';
import { ADMIN_CONTRACT_ROUTES } from '@/lib/api-transport/contract-routes';
import { getRequestConstructor } from '@/lib/api-transport/request-constructor';

// POST /api/auth/ephemeral-master-key - Generate an ephemeral master key for direct API access
export async function POST() {
  try {
    // Get master key from environment
    const masterKey = process.env.CONDUIT_API_TO_API_BACKEND_AUTH_KEY;
    if (!masterKey) {
      console.error('Master key not configured');
      return NextResponse.json(
        { error: 'Master key not configured' },
        { status: 500 }
      );
    }

    // In development mode with CLERK_AUTH_ENABLED=false,
    // we return the master key directly without calling the Admin API
    // In production, this would call the Admin API to generate a real ephemeral key

    const isDevelopment = process.env.NODE_ENV === 'development' && process.env.CLERK_AUTH_ENABLED !== 'true';
    
    if (isDevelopment) {
      // Development mode: return the master key directly
      const result = {
        ephemeralMasterKey: masterKey,
        expiresAt: new Date(Date.now() + 3600000).toISOString(), // 1 hour from now
        expiresInSeconds: 3600,
        adminApiUrl: process.env.CONDUIT_ADMIN_API_EXTERNAL_URL ?? 'http://localhost:5002',
      } satisfies WebAdminEphemeralMasterKeyResponse;
      
      return NextResponse.json(result);
    }
    
    // Production mode: call the Admin API's ephemeral master key endpoint
    const adminApiUrl = process.env.CONDUIT_ADMIN_API_BASE_URL ?? 'http://admin-api:5002';
    const adminClient = createClient<AdminPaths>({
      baseUrl: adminApiUrl,
      headers: { 'X-Master-Key': masterKey },
      Request: getRequestConstructor(),
    });
    const { data, error: apiError, response: apiResponse } = await adminClient.POST(
      ADMIN_CONTRACT_ROUTES.ephemeralMasterKey,
    );

    if (apiError !== undefined || !apiResponse.ok) {
      console.error('Failed to generate ephemeral master key:', apiError);
      throw new Error(`Failed to generate ephemeral master key: ${apiResponse.status}`);
    }

    const response = parseCriticalResponse(
      adminEphemeralKeySchema,
      data,
      'Admin ephemeral master-key issuance',
    );
    
    // Return the ephemeral master key with Admin API URL
    // Use the external URL that the browser can access
    const result = {
      ...response,
      adminApiUrl: process.env.CONDUIT_ADMIN_API_EXTERNAL_URL ?? 'http://localhost:5002',
    } satisfies WebAdminEphemeralMasterKeyResponse;
    
    return NextResponse.json(result);
  } catch (error) {
    console.error('Error generating ephemeral master key:', error);
    return toApiErrorResponse(error);
  }
}
