import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type {
  ModelProviderMappingDto,
  CreateModelProviderMappingDto,
  UpdateModelProviderMappingDto,
  BulkMappingRequest,
  BulkMappingResponse,
  BulkModelMappingPreviewRequest,
  BulkModelMappingPreviewResponse,
  BulkDeleteResult,
  BulkUpdateResult,
} from '../models/modelMapping';


/**
 * Type-safe Model Mappings service using native fetch
 */
export class FetchModelMappingsService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get all model mappings
   * Note: The backend currently returns a plain array, not a paginated response
   */
  async list(
    config?: RequestConfig
  ): Promise<ModelProviderMappingDto[]> {
    const result = await this.client.executeContractRead(
      '/v1/admin/model-provider-mappings',
      (contractClient, options) => contractClient.GET('/v1/admin/model-provider-mappings', options),
      config,
    );
    return result.data;
  }

  /**
   * Get a specific model mapping by ID
   */
  async getById(id: number, config?: RequestConfig): Promise<ModelProviderMappingDto> {
    return this.client.executeContractRead(
      `/v1/admin/model-provider-mappings/${id}`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-provider-mappings/{id}', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
  }

  /**
   * Create a new model mapping
   */
  async create(
    data: CreateModelProviderMappingDto,
    config?: RequestConfig
  ): Promise<ModelProviderMappingDto> {
    return this.client.executeContractOperation<ModelProviderMappingDto, CreateModelProviderMappingDto>(
      '/v1/admin/model-provider-mappings',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-provider-mappings', {
        ...options,
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Update an existing model mapping
   */
  async update(
    id: number,
    data: UpdateModelProviderMappingDto,
    config?: RequestConfig
  ): Promise<ModelProviderMappingDto> {
    return this.client.executeContractOperation<ModelProviderMappingDto, UpdateModelProviderMappingDto>(
      `/v1/admin/model-provider-mappings/${id}`,
      HttpMethod.PATCH,
      (contractClient, options) => contractClient.PATCH('/v1/admin/model-provider-mappings/{id}', {
        ...options,
        params: { path: { id } },
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Delete a model mapping
   */
  async deleteById(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation<void>(
      `/v1/admin/model-provider-mappings/${id}`,
      HttpMethod.DELETE,
      (contractClient, options) => contractClient.DELETE('/v1/admin/model-provider-mappings/{id}', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
  }




  /**
   * Resolve associations and conflicts for discovered provider models.
   */
  async previewBulk(
    request: BulkModelMappingPreviewRequest,
    config?: RequestConfig
  ): Promise<BulkModelMappingPreviewResponse> {
    return this.client.executeContractOperation<BulkModelMappingPreviewResponse, BulkModelMappingPreviewRequest>(
      '/v1/admin/model-provider-mappings/bulk/preview',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-provider-mappings/bulk/preview', {
        ...options,
        body: request,
      }),
      config,
      request,
    );
  }

  /**
   * Bulk create model mappings
   */
  async bulkCreate(
    request: BulkMappingRequest,
    config?: RequestConfig
  ): Promise<BulkMappingResponse> {
    return this.client.executeContractOperation<BulkMappingResponse, BulkMappingRequest>(
      '/v1/admin/model-provider-mappings/bulk',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-provider-mappings/bulk', {
        ...options,
        body: request,
      }),
      config,
      request,
    );
  }

  /**
   * Bulk delete model mappings
   */
  async bulkDelete(
    ids: number[],
    config?: RequestConfig
  ): Promise<BulkDeleteResult> {
    return this.client.executeContractOperation<BulkDeleteResult, number[]>(
      '/v1/admin/model-provider-mappings/bulk/delete',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-provider-mappings/bulk/delete', {
        ...options,
        body: ids,
      }),
      config,
      ids,
    );
  }

  /**
   * Bulk enable model mappings
   */
  async bulkEnable(
    ids: number[],
    config?: RequestConfig
  ): Promise<BulkUpdateResult> {
    return this.client.executeContractOperation<BulkUpdateResult, number[]>(
      '/v1/admin/model-provider-mappings/bulk/enable',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-provider-mappings/bulk/enable', {
        ...options,
        body: ids,
      }),
      config,
      ids,
    );
  }

  /**
   * Bulk disable model mappings
   */
  async bulkDisable(
    ids: number[],
    config?: RequestConfig
  ): Promise<BulkUpdateResult> {
    return this.client.executeContractOperation<BulkUpdateResult, number[]>(
      '/v1/admin/model-provider-mappings/bulk/disable',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-provider-mappings/bulk/disable', {
        ...options,
        body: ids,
      }),
      config,
      ids,
    );
  }

}
