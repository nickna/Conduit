import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type { components, paths } from '@/generated/admin-api';

type ProviderErrorDto = components['schemas']['ProviderErrorDto'];
type ProviderErrorSummaryDto = components['schemas']['ProviderErrorSummaryDto'];
type ErrorStatisticsDto = components['schemas']['ErrorStatisticsDto'];
type KeyErrorDetailsDto = components['schemas']['KeyErrorDetailsDto'];
type ClearErrorsRequest = components['schemas']['ClearErrorsRequest'];
type ClearKeyErrorsResponseDto = components['schemas']['ClearKeyErrorsResponseDto'];
type RecentQuery = paths['/v1/admin/provider-errors/recent']['get']['parameters']['query'];

export class FetchProviderErrorsService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async getRecentErrors(params?: RecentQuery, config?: RequestConfig): Promise<ProviderErrorDto[]> {
    const queryString = new URLSearchParams(Object.entries(params ?? {}).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)])).toString();
    return this.client.executeContractRead(`/v1/admin/provider-errors/recent${queryString ? `?${queryString}` : ''}`,
      (contractClient, options) => contractClient.GET('/v1/admin/provider-errors/recent', { ...options, params: { query: params } }), config);
  }

  async getSummary(config?: RequestConfig): Promise<ProviderErrorSummaryDto[]> {
    const result = await this.client.executeContractRead('/v1/admin/provider-errors/summary',
      (contractClient, options) => contractClient.GET('/v1/admin/provider-errors/summary', options), config);
    return result.data;
  }

  async getStatistics(hours = 24, config?: RequestConfig): Promise<ErrorStatisticsDto> {
    return this.client.executeContractRead(`/v1/admin/provider-errors/stats?hours=${hours}`,
      (contractClient, options) => contractClient.GET('/v1/admin/provider-errors/stats', { ...options, params: { query: { hours } } }), config);
  }

  async getKeyErrors(keyId: number, config?: RequestConfig): Promise<KeyErrorDetailsDto> {
    return this.client.executeContractRead(`/v1/admin/provider-errors/keys/${keyId}`,
      (contractClient, options) => contractClient.GET('/v1/admin/provider-errors/keys/{keyId}', { ...options, params: { path: { keyId } } }), config);
  }

  async clearKeyErrors(keyId: number, request: ClearErrorsRequest, config?: RequestConfig): Promise<ClearKeyErrorsResponseDto> {
    return this.client.executeContractOperation(`/v1/admin/provider-errors/keys/${keyId}/clear`, HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/provider-errors/keys/{keyId}/clear', { ...options, params: { path: { keyId } }, body: request }), config, request);
  }
}
