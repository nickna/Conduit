import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { components } from '@/generated/admin-api';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type {
  SystemInfoDto,
  HealthStatusDto
} from '../models/system';

const WEBADMIN_SETTING_KEY = 'WebAdmin_VirtualKey';
const WEBADMIN_GROUP_EXTERNAL_ID = 'webadmin-internal';
let webAdminVirtualKeyPromise: Promise<string> | null = null;
export type ServiceHealthResponse = components['schemas']['ServiceHealthResponse'];

/**
 * Type-safe System service using native fetch
 */
export class FetchSystemService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get system information
   */
  async getSystemInfo(config?: RequestConfig): Promise<SystemInfoDto> {
    return this.client.executeContractRead(
      '/v1/admin/system-metadata/info',
      (client, options) => client.GET('/v1/admin/system-metadata/info', options),
      config,
    );
  }

  /**
   * Get system health status
   */
  async getHealth(config?: RequestConfig): Promise<HealthStatusDto> {
    return this.client.executeContractRead(
      '/v1/admin/system-metadata/health',
      (client, options) => client.GET('/v1/admin/system-metadata/health', options),
      config,
    );
  }

  /**
   * Invalidates all discovery cache entries
   * @returns Promise with cache invalidation result
   */
  async invalidateDiscoveryCache(config?: RequestConfig): Promise<{ message: string; timestamp: string; note?: string }> {
    return this.client.executeContractOperation(
      '/v1/admin/system-metadata/cache/invalidate-discovery',
      HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/system-metadata/cache/invalidate-discovery', options),
      config,
    );
  }

  /** Gets logical service, dependency, and per-instance health. */
  async getServiceHealth(config?: RequestConfig): Promise<ServiceHealthResponse> {
    return this.client.executeContractRead(
      '/v1/admin/health-status/services',
      (contractClient, options) => contractClient.GET('/v1/admin/health-status/services', options),
      config,
    );
  }

  /** Publishes an invalidation request for cached function tool definitions. */
  async invalidateFunctionDiscoveryCache(
    config?: RequestConfig,
  ): Promise<{ message: string; timestamp: string; note?: string }> {
    return this.client.executeContractOperation(
      '/v1/admin/system-metadata/cache/invalidate-function-discovery',
      HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/system-metadata/cache/invalidate-function-discovery', options),
      config,
    );
  }

  /**
   * Gets or creates the special WebAdmin virtual key.
   * This key is stored unencrypted in GlobalSettings for WebAdmin/TUI access.
   * @returns The actual (unhashed) virtual key value
   */
  async getWebAdminVirtualKey(config?: RequestConfig): Promise<string> {
    webAdminVirtualKeyPromise ??= this.getOrCreateWebAdminVirtualKey(config);

    const currentPromise = webAdminVirtualKeyPromise;
    try {
      return await currentPromise;
    } finally {
      if (webAdminVirtualKeyPromise === currentPromise) {
        webAdminVirtualKeyPromise = null;
      }
    }
  }

  private async getOrCreateWebAdminVirtualKey(config?: RequestConfig): Promise<string> {
    // Import services we need
    const { FetchSettingsService } = await import('./FetchSettingsService');
    const { FetchVirtualKeyService } = await import('./FetchVirtualKeyService');
    const { FetchVirtualKeyGroupService } = await import('./FetchVirtualKeyGroupService');

    const settingsService = new FetchSettingsService(this.client);
    const virtualKeyService = new FetchVirtualKeyService(this.client);

    let existingKey: string | null = null;

    try {
      // First try to get existing key from GlobalSettings
      const setting = await settingsService.getGlobalSetting(WEBADMIN_SETTING_KEY, config);
      if (setting?.value) {
        existingKey = setting.value;
        console.warn('[API] Found WebAdmin virtual key in GlobalSettings, validating...');

        // Validate that the key exists in VirtualKeys table
        try {
          // Try to validate the key by checking if it works
          const validationResult = await virtualKeyService.validate(existingKey, config);

          if (!validationResult?.isValid) {
            console.warn('[API] WebAdmin virtual key from GlobalSettings is not valid');
            existingKey = null;
          } else {
            console.warn('[API] WebAdmin virtual key validated successfully');
            return existingKey;
          }
        } catch (validationError) {
          console.error('[API] Failed to validate WebAdmin virtual key', validationError);
          existingKey = null;
        }
      }
    } catch {
      // Key doesn't exist in GlobalSettings
      console.warn('[API] WebAdmin virtual key not found in GlobalSettings');
    }

    // If we don't have a valid key, create a new one
    console.warn('[API] Creating new WebAdmin virtual key with group and $1000 balance');

    const virtualKeyGroupService = new FetchVirtualKeyGroupService(this.client);
    let group: components['schemas']['VirtualKeyGroupDto'] | undefined;
    let createdGroup = false;

    try {
      const firstPage = await virtualKeyGroupService.list({ page: 1, pageSize: 100 }, config);
      group = firstPage.data?.find(item => item.externalGroupId === WEBADMIN_GROUP_EXTERNAL_ID);
      for (let page = 2; !group && page <= (firstPage.pagination?.totalPages ?? 1); page++) {
        const nextPage = await virtualKeyGroupService.list({ page, pageSize: 100 }, config);
        group = nextPage.data?.find(item => item.externalGroupId === WEBADMIN_GROUP_EXTERNAL_ID);
      }
    } catch (error) {
      console.warn('[API] Failed to search for an existing WebAdmin virtual key group', error);
    }

    if (!group) {
      group = await virtualKeyGroupService.create({
        groupName: 'WebAdmin Internal Group',
        externalGroupId: WEBADMIN_GROUP_EXTERNAL_ID,
        initialBalance: 1000.00
      }, config);
      createdGroup = true;
      console.warn(`[API] Created WebAdmin virtual key group with ID ${group.id} and $1000 balance`);
    } else {
      console.warn(`[API] Reusing WebAdmin virtual key group with ID ${group.id}`);
    }

    // Create metadata
    const metadata = {
      visibility: 'hidden',
      created: new Date().toISOString(),
      originator: 'WebAdmin API client',
      groupId: group.id
    };

    // Create the virtual key and associate it with the group
    const virtualKeyRequest = {
      keyName: 'WebAdmin Internal Key',
      metadata,
      virtualKeyGroupId: group.id
    } as components['schemas']['CreateVirtualKeyRequestDto'];

    const response = await virtualKeyService.create(virtualKeyRequest, config);

    if (!response.virtualKey) {
      throw new Error('Failed to create virtual key: No key returned');
    }

    // Store the unhashed key in GlobalSettings
    try {
      await settingsService.updateGlobalSetting(
        WEBADMIN_SETTING_KEY,
        response.virtualKey,
        'Virtual key for WebAdmin Gateway API access',
        config,
      );
    } catch (persistError) {
      try {
        const winner = await settingsService.getGlobalSetting(WEBADMIN_SETTING_KEY, config);
        if (winner?.value) {
          const validation = await virtualKeyService.validate(winner.value, config);
          if (validation?.isValid) {
            try {
              await virtualKeyService.delete(String(response.keyInfo.id), config);
              if (createdGroup) {
                await virtualKeyGroupService.delete(group.id, config);
              }
            } catch (cleanupError) {
              console.error('[API] Failed to clean up losing WebAdmin bootstrap resources', cleanupError);
            }
            return winner.value;
          }
        }
      } catch {
        // Preserve the original settings write error when no valid winner can be loaded.
      }

      throw persistError;
    }

    console.warn('[API] Created new WebAdmin virtual key and stored in GlobalSettings');
    return response.virtualKey;
  }
}
