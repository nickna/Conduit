import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type { components, paths } from '@/generated/admin-api';
import type { DriftItemFilter } from '../models/providerSync';

type DriftItemDto = components['schemas']['DriftItemDto'];
type DriftActionResultDto = components['schemas']['DriftActionResultDto'];
type BulkDriftActionRequest = components['schemas']['BulkDriftActionRequest'];
type BulkDriftActionResponse = components['schemas']['BulkDriftActionResponse'];
type ProviderSyncRunDto = components['schemas']['ProviderSyncRunDto'];
type DriftQuery = paths['/v1/admin/provider-sync-jobs/drift']['get']['parameters']['query'];
type RunsQuery = paths['/v1/admin/provider-sync-jobs/runs']['get']['parameters']['query'];

export class FetchProviderSyncService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async listDrift(filter?: DriftItemFilter, config?: RequestConfig): Promise<DriftItemDto[]> {
    const query: DriftQuery = filter;
    const queryString = new URLSearchParams(Object.entries(filter ?? {}).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)])).toString();
    const result = await this.client.executeContractRead(
      `/v1/admin/provider-sync-jobs/drift${queryString ? `?${queryString}` : ''}`,
      (contractClient, options) => contractClient.GET('/v1/admin/provider-sync-jobs/drift', { ...options, params: { query } }),
      config,
    );
    return result.data;
  }

  async getDrift(id: number, config?: RequestConfig): Promise<DriftItemDto> {
    return this.client.executeContractRead(`/v1/admin/provider-sync-jobs/drift/${id}`,
      (contractClient, options) => contractClient.GET('/v1/admin/provider-sync-jobs/drift/{id}', { ...options, params: { path: { id } } }), config);
  }

  async apply(id: number, config?: RequestConfig): Promise<DriftActionResultDto> {
    return this.client.executeContractOperation(`/v1/admin/provider-sync-jobs/drift/${id}/apply`, HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/provider-sync-jobs/drift/{id}/apply', { ...options, params: { path: { id } } }), config);
  }

  async dismiss(id: number, config?: RequestConfig): Promise<DriftActionResultDto> {
    return this.client.executeContractOperation(`/v1/admin/provider-sync-jobs/drift/${id}/dismiss`, HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/provider-sync-jobs/drift/{id}/dismiss', { ...options, params: { path: { id } } }), config);
  }

  async applyBulk(ids: number[], config?: RequestConfig): Promise<BulkDriftActionResponse> {
    const body: BulkDriftActionRequest = { ids };
    return this.client.executeContractOperation('/v1/admin/provider-sync-jobs/drift/bulk/apply', HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/provider-sync-jobs/drift/bulk/apply', { ...options, body }), config, body);
  }

  async dismissBulk(ids: number[], config?: RequestConfig): Promise<BulkDriftActionResponse> {
    const body: BulkDriftActionRequest = { ids };
    return this.client.executeContractOperation('/v1/admin/provider-sync-jobs/drift/bulk/dismiss', HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/provider-sync-jobs/drift/bulk/dismiss', { ...options, body }), config, body);
  }

  async run(config?: RequestConfig): Promise<ProviderSyncRunDto> {
    return this.client.executeContractOperation('/v1/admin/provider-sync-jobs/run', HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/provider-sync-jobs/run', options), config);
  }

  async listRuns(page = 1, pageSize = 25, config?: RequestConfig): Promise<ProviderSyncRunDto[]> {
    const query: RunsQuery = { page, pageSize };
    const result = await this.client.executeContractRead(`/v1/admin/provider-sync-jobs/runs?page=${page}&pageSize=${pageSize}`,
      (contractClient, options) => contractClient.GET('/v1/admin/provider-sync-jobs/runs', { ...options, params: { query } }), config);
    return result.data;
  }
}
