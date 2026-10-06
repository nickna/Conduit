import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { components } from '@/generated/admin-api';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';

// Type aliases for better readability
type ModelSeriesDto = components['schemas']['ModelSeriesDto'];
type CreateModelSeriesDto = components['schemas']['CreateModelSeriesDto'];
type UpdateModelSeriesDto = components['schemas']['UpdateModelSeriesDto'];
type SeriesSimpleModelDto = components['schemas']['SeriesSimpleModelDto'];

/**
 * Type-safe Model Series service using the local Admin transport.
 */
export class FetchModelSeriesService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get all model series
   */
  async list(config?: RequestConfig): Promise<ModelSeriesDto[]> {
    const result = await this.client.executeContractRead(
      '/v1/admin/model-series',
      (contractClient, options) => contractClient.GET('/v1/admin/model-series', options),
      config,
    );
    return result.data;
  }

  /**
   * Get a specific model series by ID
   */
  async get(id: number, config?: RequestConfig): Promise<ModelSeriesDto> {
    return this.client.executeContractRead(
      `/v1/admin/model-series/${id}`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-series/{id}', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
  }

  /**
   * Get models in a series
   */
  async getModels(id: number, config?: RequestConfig): Promise<SeriesSimpleModelDto[]> {
    const result = await this.client.executeContractRead(
      `/v1/admin/model-series/${id}/models`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-series/{id}/models', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
    return result.data;
  }

  /**
   * Create a new model series
   */
  async create(
    data: CreateModelSeriesDto,
    config?: RequestConfig
  ): Promise<ModelSeriesDto> {
    return this.client.executeContractOperation<ModelSeriesDto, CreateModelSeriesDto>(
      '/v1/admin/model-series',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-series', {
        ...options,
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Update an existing model series
   */
  async update(
    id: number,
    data: UpdateModelSeriesDto,
    config?: RequestConfig
  ): Promise<ModelSeriesDto> {
    return this.client.executeContractOperation<ModelSeriesDto, UpdateModelSeriesDto>(
      `/v1/admin/model-series/${id}`,
      HttpMethod.PATCH,
      (contractClient, options) => contractClient.PATCH('/v1/admin/model-series/{id}', {
        ...options,
        params: { path: { id } },
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Delete a model series
   */
  async delete(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation<void>(
      `/v1/admin/model-series/${id}`,
      HttpMethod.DELETE,
      (contractClient, options) => contractClient.DELETE('/v1/admin/model-series/{id}', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
  }
}
