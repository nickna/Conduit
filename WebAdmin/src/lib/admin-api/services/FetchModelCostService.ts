import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import type { components } from '@/generated/admin-api';
import { HttpMethod } from '../client/HttpMethod';
import {
  ModelCostDto,
  CreateModelCostDto,
  UpdateModelCostDto,
  ModelCostImportResult,
} from '../models/modelCost';
import { PagedResult } from '../models/common-types';
import { ValidationError } from '../utils/errors';
import { validateRequired, validateStringLength, validateNonEmptyArray, validateNumberRange } from '../utils/validation';

// Type aliases for better readability
interface ModelCostListParams {
  page?: number;
  pageSize?: number;
  provider?: string;
  isActive?: boolean;
  /** Filter by model type (chat, image, video, embedding, audio) */
  modelType?: string;
}

type ContractPagedModelCosts = components['schemas']['PagedResultOfModelCostDto'];
type ContractBulkImportResult = components['schemas']['BulkImportResult'];
type ContractModelCost = components['schemas']['ModelCostDto'];
type ContractCreateModelCost = components['schemas']['CreateModelCostDto'];
type ContractUpdateModelCost = components['schemas']['UpdateModelCostDto'];

const parsePricingConfiguration = (
  value: string | undefined,
): Record<string, unknown> | undefined => {
  if (!value?.trim()) return undefined;
  const parsed: unknown = JSON.parse(value);
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new ValidationError('pricingConfiguration must be a valid JSON object');
  }
  return parsed as Record<string, unknown>;
};

const modelCostFromWire = (value: ContractModelCost): ModelCostDto => ({
  ...value,
  pricingConfiguration: JSON.stringify(value.pricingConfiguration ?? {}),
}) as unknown as ModelCostDto;

/**
 * Validates create model cost request
 */
function validateCreateModelCostRequest(data: CreateModelCostDto): void {
  // Validate required fields
  validateRequired(data, ['costName', 'modelProviderTypeAssociationIds', 'inputCostPerMillionTokens', 'outputCostPerMillionTokens']);
  validateStringLength(data.costName, 1, 255, 'costName');
  validateNonEmptyArray(data.modelProviderTypeAssociationIds ?? [], 'modelProviderTypeAssociationIds');

  // Validate cost values are non-negative
  if (data.inputCostPerMillionTokens < 0) {
    throw new ValidationError('inputCostPerMillionTokens must be non-negative');
  }
  if (data.outputCostPerMillionTokens < 0) {
    throw new ValidationError('outputCostPerMillionTokens must be non-negative');
  }

  // Validate optional number fields if provided
  const optionalNumberFields = [
    'embeddingCostPerMillionTokens',
    'audioCostPerMinute',
    'audioCostPerThousandCharacters',
    'cachedInputCostPerMillionTokens',
    'cachedInputWriteCostPerMillionTokens',
    'costPerSearchUnit',
  ] as const;

  for (const field of optionalNumberFields) {
    const value = data[field as keyof CreateModelCostDto];
    if (value !== undefined && value !== null && typeof value === 'number' && value < 0) {
      throw new ValidationError(`${field} must be non-negative`);
    }
  }

  // Validate batch processing multiplier is between 0 and 1
  if (data.batchProcessingMultiplier !== undefined && data.batchProcessingMultiplier !== null) {
    validateNumberRange(data.batchProcessingMultiplier, 0, 1, 'batchProcessingMultiplier');
  }
}

/**
 * Type-safe Model Cost service using native fetch
 */
export class FetchModelCostService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get all model costs with optional pagination and filtering
   */
  async list(
    params?: ModelCostListParams,
    config?: RequestConfig
  ): Promise<PagedResult<ModelCostDto>> {
    const providerId = params?.provider === undefined ? undefined : Number(params.provider);
    if (providerId !== undefined && (!Number.isInteger(providerId) || providerId < 1))
      throw new ValidationError('provider must be a positive numeric provider ID');
    const query = { page: params?.page, pageSize: params?.pageSize, providerId,
      isActive: params?.isActive, modelType: params?.modelType };
    const queryString = new URLSearchParams(Object.entries(query).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)])).toString();
    const result: ContractPagedModelCosts = await this.client.executeContractRead(
      `/v1/admin/model-costs${queryString ? `?${queryString}` : ''}`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-costs', { ...options, params: { query } }), config);
    return {
      items: result.data.map(modelCostFromWire),
      totalCount: result.pagination?.totalItems ?? result.data.length,
      page: result.pagination?.page ?? params?.page ?? 1,
      pageSize: result.pagination?.pageSize ?? params?.pageSize ?? 50,
      totalPages: result.pagination?.totalPages ?? 1,
    };
  }

  /**
   * Get a specific model cost by ID
   */
  async getById(id: number, config?: RequestConfig): Promise<ModelCostDto> {
    const result = await this.client.executeContractRead(`/v1/admin/model-costs/${id}`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-costs/{id}', { ...options, params: { path: { id } } }), config);
    return modelCostFromWire(result);
  }


  /**
   * Create a new model cost configuration
   */
  async create(
    data: CreateModelCostDto,
    config?: RequestConfig
  ): Promise<ModelCostDto> {
    validateCreateModelCostRequest(data);

    const body = {
      ...data,
      pricingConfiguration: parsePricingConfiguration(data.pricingConfiguration) ?? {},
    } as unknown as ContractCreateModelCost;
    const result = await this.client.executeContractOperation<ContractModelCost, ContractCreateModelCost>('/v1/admin/model-costs', HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-costs', { ...options, body }), config, body);
    return modelCostFromWire(result);
  }

  /**
   * Update an existing model cost configuration
   */
  async update(
    id: number,
    data: UpdateModelCostDto,
    config?: RequestConfig
  ): Promise<ModelCostDto> {
    const body = {
      ...data,
      pricingConfiguration: parsePricingConfiguration(data.pricingConfiguration),
    } as unknown as ContractUpdateModelCost;
    const result = await this.client.executeContractOperation<ContractModelCost, ContractUpdateModelCost>(`/v1/admin/model-costs/${id}`, HttpMethod.PATCH,
      (contractClient, options) => contractClient.PATCH('/v1/admin/model-costs/{id}', { ...options, params: { path: { id } }, body }), config, body);
    return modelCostFromWire(result);
  }

  /**
   * Delete a model cost configuration
   */
  async deleteById(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation<void>(`/v1/admin/model-costs/${id}`, HttpMethod.DELETE,
      (contractClient, options) => contractClient.DELETE('/v1/admin/model-costs/{id}', { ...options, params: { path: { id } } }), config);
  }

  /**
   * Import multiple model costs at once
   */
  async import(
    modelCosts: CreateModelCostDto[],
    config?: RequestConfig
  ): Promise<ModelCostImportResult> {
    const result: ContractBulkImportResult = await this.client.executeContractOperation('/v1/admin/model-costs/import', HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-costs/import', { ...options, body: modelCosts as ContractCreateModelCost[] }), config, modelCosts);
    return { success: result.successCount ?? 0, failed: result.failureCount ?? 0,
      errors: (result.errors ?? []).map((error, index) => ({ row: index + 1, error })) };
  }

}
