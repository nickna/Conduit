import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { components } from '@/generated/admin-api';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type {
  PromptCachingAnalyticsDto,
  PromptCachingCapabilityDto,
  PromptCachingConfigDto,
  UpdatePromptCachingConfigDto,
} from '../models/promptCaching';

type RoutingConfigurationDto = components['schemas']['RoutingConfigurationDto'];
type RoutingDefaultsDto = components['schemas']['RoutingDefaultsDto'];
type RoutePolicyDto = components['schemas']['RoutePolicyDto'];

/** Contract-native Configuration and prompt-caching operations. */
export class FetchConfigurationService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async getRoutingConfiguration(config?: RequestConfig): Promise<RoutingConfigurationDto> {
    return this.client.executeContractRead(
      '/v1/admin/routing-configurations/routing',
      (client, options) => client.GET('/v1/admin/routing-configurations/routing', options),
      config,
    );
  }

  async getRoutingDefaults(config?: RequestConfig): Promise<RoutingDefaultsDto> {
    return this.client.executeContractRead(
      '/v1/admin/routing-configurations/routing/defaults',
      (client, options) => client.GET('/v1/admin/routing-configurations/routing/defaults', options),
      config,
    );
  }

  async updateRoutingDefaults(
    data: RoutingDefaultsDto,
    config?: RequestConfig,
  ): Promise<RoutingDefaultsDto> {
    return this.client.executeContractOperation(
      '/v1/admin/routing-configurations/routing/defaults',
      HttpMethod.PUT,
      (client, options) => client.PUT('/v1/admin/routing-configurations/routing/defaults', { ...options, body: data }),
      config,
      data,
    );
  }

  async getAliasRouting(alias: string, config?: RequestConfig): Promise<RoutePolicyDto> {
    return this.client.executeContractRead(
      `/v1/admin/routing-configurations/routing/aliases/${encodeURIComponent(alias)}`,
      (client, options) => client.GET('/v1/admin/routing-configurations/routing/aliases/{alias}', {
        ...options,
        params: { path: { alias } },
      }),
      config,
    );
  }

  async updateAliasRouting(
    alias: string,
    data: RoutePolicyDto,
    config?: RequestConfig,
  ): Promise<RoutePolicyDto> {
    return this.client.executeContractOperation(
      `/v1/admin/routing-configurations/routing/aliases/${encodeURIComponent(alias)}`,
      HttpMethod.PUT,
      (client, options) => client.PUT('/v1/admin/routing-configurations/routing/aliases/{alias}', {
        ...options,
        params: { path: { alias } },
        body: data,
      }),
      config,
      data,
    );
  }

  async getPromptCachingConfig(config?: RequestConfig): Promise<PromptCachingConfigDto> {
    return this.client.executeContractRead(
      '/v1/admin/prompt-cache-settings/config',
      (client, options) => client.GET('/v1/admin/prompt-cache-settings/config', options),
      config,
    ) as Promise<PromptCachingConfigDto>;
  }

  async updatePromptCachingConfig(
    data: UpdatePromptCachingConfigDto,
    config?: RequestConfig,
  ): Promise<PromptCachingConfigDto> {
    return this.client.executeContractOperation(
      '/v1/admin/prompt-cache-settings/config',
      HttpMethod.PUT,
      (client, options) => client.PUT('/v1/admin/prompt-cache-settings/config', { ...options, body: data }),
      config,
      data,
    ) as Promise<PromptCachingConfigDto>;
  }

  async getPromptCachingCapabilities(config?: RequestConfig): Promise<PromptCachingCapabilityDto[]> {
    const result = await this.client.executeContractRead(
      '/v1/admin/prompt-cache-settings/capabilities',
      (client, options) => client.GET('/v1/admin/prompt-cache-settings/capabilities', options),
      config,
    );
    return result.data as PromptCachingCapabilityDto[];
  }

  async getPromptCachingAnalytics(
    filters: { from?: string; to?: string; alias?: string; provider?: string; mappingId?: number } = {},
    config?: RequestConfig,
  ): Promise<PromptCachingAnalyticsDto> {
    const query = Object.fromEntries(
      Object.entries(filters).filter(([, value]) => value !== undefined && value !== ''),
    );
    const search = new URLSearchParams(Object.entries(query).map(([key, value]) => [key, String(value)]));
    return this.client.executeContractRead(
      `/v1/admin/prompt-cache-settings/analytics${search.size ? `?${search}` : ''}`,
      (client, options) => client.GET('/v1/admin/prompt-cache-settings/analytics', {
        ...options,
        params: { query },
      }),
      config,
    ) as Promise<PromptCachingAnalyticsDto>;
  }
}
