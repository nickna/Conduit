import { useQuery } from '@tanstack/react-query';
import { getBrowserGatewayClient } from '@/lib/client/browserGatewayClient';
import { ModelCapability, type DiscoveredModel as SDKDiscoveredModel } from '@/lib/gateway-api';

// Extend the SDK type to include backend fields not in the generated types
export interface DiscoveryModel extends SDKDiscoveredModel {
  parameters?: string;
  max_tokens?: number;
  max_output_tokens?: number;
}

export interface DiscoveryResponse {
  data: DiscoveryModel[];
  count: number;
}

export function useDiscoveryModels(capability?: ModelCapability | string) {
  return useQuery<DiscoveryResponse>({
    queryKey: ['discovery-models', capability],
    queryFn: async () => {
      try {
        // Get the browser client with ephemeral key
        const client = await getBrowserGatewayClient();
        
        // Use the SDK directly - let the backend handle filtering
        const response = capability 
          ? await client.discovery.getModelsByCapability(capability)
          : await client.discovery.getModels();
        
        // The SDK response matches our interface, just return it
        return response as DiscoveryResponse;
      } catch (error) {
        console.error('Discovery API error:', error);
        // Pass through the original error message instead of wrapping it
        if (error instanceof Error) {
          throw error;
        }
        throw new Error('An error occurred while fetching models');
      }
    },
    staleTime: 30 * 1000, // 30 seconds - short cache to quickly reflect model mapping changes
    retry: 3,
    retryDelay: (attemptIndex) => Math.min(1000 * 2 ** attemptIndex, 30000),
  });
}