import { useMemo } from 'react';
import { useModelMappings } from '@/hooks/useModelMappingsApi';
import type { ModelCostDto } from '@/lib/admin-api';

interface ProviderInfo {
  providerId: number;
  providerName: string;
  providerType: string;
}

export interface EnrichedModelCost extends ModelCostDto {
  providers: ProviderInfo[];
}

export function useEnrichedModelCosts(modelCosts: ModelCostDto[] | undefined) {
  const { mappings, isLoading: mappingsLoading } = useModelMappings();

  const enrichedCosts = useMemo(() => {
    if (!modelCosts || !mappings || mappings.length === 0) {
      return modelCosts?.map(cost => ({
        ...cost,
        providers: []
      })) ?? [];
    }

    return modelCosts.map(cost => {
      // Find all unique providers for this cost's model aliases
      const providersMap = new Map<number, ProviderInfo>();
      
      cost.associatedModelAliases.forEach(alias => {
        const mapping = mappings.find(m => m.modelAlias === alias || m.providerModelId === alias);
        
        if (mapping) {
          const providerId = mapping.providerId;
          if (!providersMap.has(providerId)) {
            providersMap.set(providerId, {
              providerId,
              providerName: mapping.provider?.displayName ?? `Provider ${providerId}`,
              providerType: mapping.provider?.providerType ?? 'Unknown'
            });
          }
        }
      });

      return {
        ...cost,
        providers: Array.from(providersMap.values())
      };
    });
  }, [modelCosts, mappings]);

  return {
    enrichedCosts,
    isLoading: mappingsLoading
  };
}
