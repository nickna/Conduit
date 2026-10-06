import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { components } from '@/generated/admin-api';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';

// Type aliases for better readability
type ModelAuthorDto = components['schemas']['ModelAuthorDto'];
type CreateModelAuthorDto = components['schemas']['CreateModelAuthorDto'];
type UpdateModelAuthorDto = components['schemas']['UpdateModelAuthorDto'];
type SimpleModelSeriesDto = components['schemas']['SimpleModelSeriesDto'];

/**
 * Type-safe Model Author service using the local Admin transport.
 */
export class FetchModelAuthorService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get all model authors
   */
  async list(config?: RequestConfig): Promise<ModelAuthorDto[]> {
    const result = await this.client.executeContractRead(
      '/v1/admin/model-authors',
      (contractClient, options) => contractClient.GET('/v1/admin/model-authors', options),
      config,
    );
    return result.data;
  }

  /**
   * Get a specific model author by ID
   */
  async get(id: number, config?: RequestConfig): Promise<ModelAuthorDto> {
    return this.client.executeContractRead(
      `/v1/admin/model-authors/${id}`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-authors/{id}', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
  }

  /**
   * Get series by author
   */
  async getSeries(id: number, config?: RequestConfig): Promise<SimpleModelSeriesDto[]> {
    const result = await this.client.executeContractRead(
      `/v1/admin/model-authors/${id}/series`,
      (contractClient, options) => contractClient.GET('/v1/admin/model-authors/{id}/series', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
    return result.data;
  }

  // Author-management views fetch authors, series, and models once each and derive counts locally.
  // If list payload size becomes material, add aggregate counts to the list DTOs rather than an
  // author-specific endpoint that would reintroduce per-author requests.

  /**
   * Create a new model author
   */
  async create(
    data: CreateModelAuthorDto,
    config?: RequestConfig
  ): Promise<ModelAuthorDto> {
    return this.client.executeContractOperation<ModelAuthorDto, CreateModelAuthorDto>(
      '/v1/admin/model-authors',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/model-authors', {
        ...options,
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Update an existing model author
   */
  async update(
    id: number,
    data: UpdateModelAuthorDto,
    config?: RequestConfig
  ): Promise<ModelAuthorDto> {
    return this.client.executeContractOperation<ModelAuthorDto, UpdateModelAuthorDto>(
      `/v1/admin/model-authors/${id}`,
      HttpMethod.PATCH,
      (contractClient, options) => contractClient.PATCH('/v1/admin/model-authors/{id}', {
        ...options,
        params: { path: { id } },
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Delete a model author
   */
  async delete(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation<void>(
      `/v1/admin/model-authors/${id}`,
      HttpMethod.DELETE,
      (contractClient, options) => contractClient.DELETE('/v1/admin/model-authors/{id}', {
        ...options,
        params: { path: { id } },
      }),
      config,
    );
  }
}
