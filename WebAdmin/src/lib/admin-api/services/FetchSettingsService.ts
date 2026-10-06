import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type {
  GlobalSettingDto,
  CreateGlobalSettingDto,
  UpdateGlobalSettingByKeyDto,
  GlobalSettingCacheStats,
  GlobalSettingDefinitionDto,
  GlobalSettingsReloadAcceptedResponse,
} from '../models/settings';

// Define the batch update types that match the issue requirements
export interface SettingUpdate {
  key: string;
  value: unknown;
}

export interface SettingsDto {
  settings: GlobalSettingDto[];
  categories: string[];
  lastModified: string;
}

/**
 * Type-safe Settings service using the generated Admin contract.
 */
export class FetchSettingsService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get all global settings
   */
  async getGlobalSettings(config?: RequestConfig): Promise<SettingsDto> {
    const readPage = (page: number) => this.client.executeContractRead(
      '/v1/admin/global-settings',
      (contractClient, options) => contractClient.GET('/v1/admin/global-settings', {
        ...options,
        params: { query: { page, pageSize: 100 } },
      }),
      config,
    );
    const firstPage = await readPage(1);
    const settings = [...firstPage.data];
    for (let page = 2; page <= firstPage.pagination.totalPages; page++) {
      settings.push(...(await readPage(page)).data);
    }

    // The API does not return category metadata, so no categories can be derived.
    const categories: string[] = [];

    // Find the most recent update
    const lastModified = settings
      .map(s => s.updatedAt)
      .sort((a, b) => new Date(b).getTime() - new Date(a).getTime())[0] || new Date().toISOString();

    return {
      settings,
      categories,
      lastModified,
    };
  }

  /** Gets the server-owned registry used to render and validate typed settings. */
  async getDefinitions(config?: RequestConfig): Promise<GlobalSettingDefinitionDto[]> {
    const result = await this.client.executeContractRead(
      '/v1/admin/global-settings/definitions',
      (contractClient, options) => contractClient.GET('/v1/admin/global-settings/definitions', {
        ...options,
        params: { query: { page: 1, pageSize: 100 } },
      }),
      config,
    );
    return result.data as GlobalSettingDefinitionDto[];
  }

  /**
   * Get a specific setting by key
   */
  async getGlobalSetting(key: string, config?: RequestConfig): Promise<GlobalSettingDto> {
    return this.client.executeContractRead(
      `/v1/admin/global-settings/by-key/${encodeURIComponent(key)}`,
      (contractClient, options) => contractClient.GET('/v1/admin/global-settings/by-key/{key}', {
        ...options,
        params: { path: { key } },
      }),
      config,
    );
  }

  /**
   * Create a new global setting
   */
  async createGlobalSetting(
    data: CreateGlobalSettingDto,
    config?: RequestConfig
  ): Promise<GlobalSettingDto> {
    return this.client.executeContractOperation<GlobalSettingDto, CreateGlobalSettingDto>(
      '/v1/admin/global-settings',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/global-settings', {
        ...options,
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Update a specific setting by key
   */
  async updateGlobalSetting(
    key: string,
    value: string,
    description?: string,
    config?: RequestConfig
  ): Promise<void> {
    const data: UpdateGlobalSettingByKeyDto = {
      key,
      value,
      description,
    };

    return this.client.executeContractOperation<void, UpdateGlobalSettingByKeyDto>(
      '/v1/admin/global-settings/by-key',
      HttpMethod.PUT,
      (contractClient, options) => contractClient.PUT('/v1/admin/global-settings/by-key', {
        ...options,
        body: data,
      }),
      config,
      data,
    );
  }

  /**
   * Delete a global setting
   */
  async deleteGlobalSetting(key: string, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation<void>(
      `/v1/admin/global-settings/by-key/${encodeURIComponent(key)}`,
      HttpMethod.DELETE,
      (contractClient, options) => contractClient.DELETE('/v1/admin/global-settings/by-key/{key}', {
        ...options,
        params: { path: { key } },
      }),
      config,
    );
  }


  /**
   * Helper method to check if a setting exists
   */
  async settingExists(key: string, config?: RequestConfig): Promise<boolean> {
    try {
      await this.getGlobalSetting(key, config);
      return true;
    } catch (error) {
      if (error && typeof error === 'object' && 'statusCode' in error && error.statusCode === 404) {
        return false;
      }
      throw error;
    }
  }

  /**
   * Get global settings cache statistics
   */
  async getCacheStats(config?: RequestConfig): Promise<GlobalSettingCacheStats> {
    return this.client.executeContractRead(
      '/v1/admin/global-settings/cache/stats',
      (contractClient, options) => contractClient.GET('/v1/admin/global-settings/cache/stats', options),
      config,
    );
  }

  /**
   * Reload all global settings from database into cache
   */
  async reloadCache(config?: RequestConfig): Promise<GlobalSettingsReloadAcceptedResponse> {
    return this.client.executeContractOperation<GlobalSettingsReloadAcceptedResponse>(
      '/v1/admin/global-settings/cache/reload',
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/global-settings/cache/reload', options),
      config,
    );
  }

  /**
   * Invalidate a specific cached setting
   */
  async invalidateSetting(key: string, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation<void>(
      `/v1/admin/global-settings/cache/invalidate/${encodeURIComponent(key)}`,
      HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/global-settings/cache/invalidate/{key}', {
        ...options,
        params: { path: { key } },
      }),
      config,
    );
  }
}
