import { FilterOptions } from './common';
import type { components } from '@/generated/admin-api';

export type ModelProviderMappingDto = components['schemas']['ModelProviderMappingDto'];
export type ModelCapabilitiesDto = components['schemas']['ModelCapabilitiesDto'];
export type CreateModelProviderMappingDto = components['schemas']['CreateModelProviderMappingDto'];
export type UpdateModelProviderMappingDto = components['schemas']['UpdateModelProviderMappingDto'];
export type BulkModelMappingItemDto = components['schemas']['BulkModelMappingItemDto'];
export type BulkModelMappingPreviewRequest = components['schemas']['BulkModelMappingPreviewRequest'];
export type BulkModelMappingPreviewResponse = components['schemas']['BulkModelMappingPreviewResponse'];
export type BulkModelMappingCreateRequest = components['schemas']['BulkModelMappingCreateRequest'];
export type BulkModelMappingCreateResponse = components['schemas']['BulkModelMappingCreateResponse'];
export type BulkModelMappingResolutionDto = components['schemas']['BulkModelMappingResolutionDto'];

// For bulk mapping requests
export type BulkMappingRequest = BulkModelMappingCreateRequest;

// For bulk mapping responses
export type BulkMappingResponse = BulkModelMappingCreateResponse;

// For bulk delete operations
export type BulkDeleteResult = components['schemas']['BulkDeleteResult'];

// For bulk update operations
export type BulkUpdateResult = components['schemas']['BulkUpdateResult'];

// For model routing information
export interface ModelRoutingInfo {
  modelAlias: string;
  providerId: number;
  providerModelId: string;
  priority: number;
}

// For model mapping suggestions
export interface ModelMappingSuggestion {
  modelAlias: string;
  providerModelId: string;
  confidence: number;
  reason?: string;
}

// For capability test results
export interface CapabilityTestResult {
  capability: string;
  supported: boolean;
  details?: string;
}

export interface ModelMappingFilterOptions extends FilterOptions {
  /**
   * Filter by model ID
   */
  modelId?: number;

  /**
   * Filter by provider ID
   */
  providerId?: number;

  /**
   * Filter by enabled status
   */
  isEnabled?: boolean;

  /**
   * Filter by minimum priority
   */
  minPriority?: number;

  /**
   * Filter by maximum priority
   */
  maxPriority?: number;
}
