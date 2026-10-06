import { NextRequest, NextResponse } from 'next/server';
import { toApiErrorResponse } from '@/lib/errors/api-errors';
import { getServerAdminClient, getServerGatewayClient } from '@/lib/server/api-client-config';
import type { WebAdminEphemeralKeyResponse } from '@/lib/api-transport/contracts';

interface EphemeralKeyRequest {
  purpose?: string; // Optional purpose for logging/tracking
}

// POST /api/auth/ephemeral-key - Generate an ephemeral key for direct API access
export async function POST(request: NextRequest) {
  try {
    const body = await request.json() as EphemeralKeyRequest;
    
    // Get the WebAdmin's virtual key from Admin API
    const adminClient = getServerAdminClient();
    let webAdminVirtualKey: string;
    
    try {
      webAdminVirtualKey = await adminClient.system.getWebAdminVirtualKey();
    } catch (error) {
      console.error('Failed to get WebAdmin virtual key:', error);
      return NextResponse.json(
        { error: 'Failed to get WebAdmin virtual key' },
        { status: 500 }
      );
    }

    // Get request metadata for tracking
    const sourceIP = request.headers.get('x-forwarded-for') ?? 
                     request.headers.get('x-real-ip') ?? 
                     'unknown';
    const userAgent = request.headers.get('user-agent') ?? 'unknown';
    
    // Use the local Gateway boundary to generate an ephemeral key.
    const coreClient = await getServerGatewayClient();
    const response = await coreClient.auth.generateEphemeralKey(webAdminVirtualKey, {
      metadata: {
        sourceIP,
        userAgent,
        purpose: body.purpose ?? 'web-admin-request'
      }
    });
    
    // Return the ephemeral key with Gateway API URL
    // Use the external URL that the browser can access
    const result = {
      ...response,
      coreApiUrl: process.env.CONDUIT_API_EXTERNAL_URL ?? 'http://localhost:5000',
    } satisfies WebAdminEphemeralKeyResponse;
    
    return NextResponse.json(result);
  } catch (error) {
    console.error('Error generating ephemeral key:', error);
    return toApiErrorResponse(error);
  }
}
