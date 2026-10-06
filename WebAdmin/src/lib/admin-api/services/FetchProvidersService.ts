import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import type { components } from '@/generated/admin-api';
import { HttpMethod } from '../client/HttpMethod';
import type { ProviderSettings } from '../models/common-types';
import {
  type ProviderDto,
  type CreateProviderDto,
  type UpdateProviderDto,
  type StandardApiKeyTestResponse
} from '../models/provider';
import { ProviderType } from '../models/providerType';
import {
  cacheProviderConfigurations,
  type ProviderConfigurationDefinition,
  type ProviderConfigurationSchema,
  type ProviderSettingField
} from '../models/providerConfiguration';
import {
  normalizeApiKeyTestResponse,
  type RawApiKeyTestResponse
} from '../utils/api-key-test-response';
import { classifyApiKeyTestError } from '../utils/error-classification';
import { FetchProvidersServiceKeys } from './FetchProvidersServiceKeys';

type ProviderListResponseDto = components['schemas']['PagedResultOfProviderDto'];
type ProviderSettingsSchemaDto = components['schemas']['ProviderSettingsSchemaDto'];
type ProviderSettingFieldDto = components['schemas']['ProviderSettingFieldDto'];

/**
 * Narrows a wire setting field, whose properties are all optional in the generated contract, into
 * the shape the form consumes. A field without a key cannot be rendered or stored, so it is dropped
 * by the caller rather than represented as a blank input.
 */
function toProviderSettingField(field: ProviderSettingFieldDto): ProviderSettingField {
  return {
    key: field.key ?? '',
    label: field.label ?? field.key ?? '',
    helpText: field.helpText ?? undefined,
    placeholder: field.placeholder ?? undefined,
    required: field.required ?? false,
    secret: field.secret ?? false,
    validationRegexSource: field.validationRegex ?? undefined,
  };
}

interface ProviderConfig {
  providerType: ProviderType;
  apiKey: string;
  baseUrl?: string;
  /** Structured, provider-scoped settings (for example a Cloudflare account ID). */
  settings?: Record<string, string>;
  additionalConfig?: ProviderSettings;
}


/**
 * Type-safe Providers service using native fetch
 */
export class FetchProvidersService {
  private readonly keysService: FetchProvidersServiceKeys;

  constructor(private readonly client: FetchBaseApiClient) {
    this.keysService = new FetchProvidersServiceKeys(client);
  }

  /**
   * Get all providers with pagination
   */
  async list(
    page: number = 1,
    pageSize: number = 50,
    config?: RequestConfig
  ): Promise<ProviderListResponseDto> {
    const query = { page, pageSize };
    return this.client.executeContractRead(`/v1/admin/providers?page=${page}&pageSize=${pageSize}`,
      (contractClient, options) => contractClient.GET('/v1/admin/providers', { ...options, params: { query } }), config);
  }

  /**
   * Get the complete backend-owned provider catalog, keyed by provider type.
   *
   * The backend registries are the single source of truth for provider choices, display names,
   * endpoint requirements, help content, and structured fields.
   */
  async getConfigurationSchema(config?: RequestConfig): Promise<ProviderConfigurationSchema> {
    const response = await this.client.executeContractRead<ProviderSettingsSchemaDto[]>(
      '/v1/admin/providers/settings-schema',
      (contractClient, options) => contractClient.GET('/v1/admin/providers/settings-schema', options), config);

    const schema: ProviderConfigurationSchema = {};
    for (const entry of response ?? []) {
      if (!entry.providerType || !entry.providerTypeId || !entry.displayName) {
        continue;
      }

      const configuration: ProviderConfigurationDefinition = {
        providerType: entry.providerType,
        providerTypeId: entry.providerTypeId,
        displayName: entry.displayName,
        requiresApiKey: entry.requiresApiKey ?? false,
        requiresEndpoint: entry.requiresEndpoint ?? false,
        supportsCustomEndpoint: entry.supportsCustomEndpoint ?? false,
        helpUrl: entry.helpUrl ?? undefined,
        helpText: entry.helpText ?? undefined,
        settings: (entry.settings ?? [])
          .map(toProviderSettingField)
          .filter(field => field.key !== ''),
      };
      schema[entry.providerType] = configuration;
    }

    cacheProviderConfigurations(schema);
    return schema;
  }

  /**
   * Get a specific provider by ID
   */
  async getById(id: number, config?: RequestConfig): Promise<ProviderDto> {
    return this.client.executeContractRead(`/v1/admin/providers/${id}`,
      (contractClient, options) => contractClient.GET('/v1/admin/providers/{id}', { ...options, params: { path: { id } } }), config);
  }

  /**
   * Create a new provider
   */
  async create(
    data: CreateProviderDto,
    config?: RequestConfig
  ): Promise<ProviderDto> {
    return this.client.executeContractOperation('/v1/admin/providers', HttpMethod.POST,
      (contractClient, options) => contractClient.POST('/v1/admin/providers', { ...options, body: data }), config, data);
  }

  /**
   * Update an existing provider
   */
  async update(
    id: number,
    data: UpdateProviderDto,
    config?: RequestConfig
  ): Promise<ProviderDto> {
    return this.client.executeContractOperation(`/v1/admin/providers/${id}`, HttpMethod.PATCH,
      (contractClient, options) => contractClient.PATCH('/v1/admin/providers/{id}', { ...options, params: { path: { id } }, body: data }), config, data);
  }

  /**
   * Delete a provider
   */
  async deleteById(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation(`/v1/admin/providers/${id}`, HttpMethod.DELETE,
      (contractClient, options) => contractClient.DELETE('/v1/admin/providers/{id}', { ...options, params: { path: { id } } }), config);
  }

  /**
   * Test connection for a specific provider
   */
  async testConnectionById(
    id: number,
    config?: RequestConfig
  ): Promise<StandardApiKeyTestResponse> {
    try {
      const result = await this.client.executeContractOperation<RawApiKeyTestResponse>(`/v1/admin/providers/${id}/test`, HttpMethod.POST,
        (contractClient, options) => contractClient.POST('/v1/admin/providers/{id}/test', { ...options, params: { path: { id } } }), config);

      // Normalize the response to handle C# PascalCase and enum mismatches
      return normalizeApiKeyTestResponse(result);
    } catch (error) {
      // Get provider info to determine type for error classification
      try {
        const provider = await this.getById(id, config);
        return classifyApiKeyTestError(error, provider.providerType);
      } catch {
        // If we can't get provider info, classify without it
        return classifyApiKeyTestError(error);
      }
    }
  }

  /**
   * Test a provider configuration without creating it
   */
  async testConfig(
    providerConfig: ProviderConfig,
    config?: RequestConfig
  ): Promise<StandardApiKeyTestResponse> {
    try {
      const result = await this.client.executeContractOperation<RawApiKeyTestResponse, ProviderConfig>('/v1/admin/providers/test', HttpMethod.POST,
        (contractClient, options) => contractClient.POST('/v1/admin/providers/test', { ...options, body: providerConfig }), config, providerConfig);

      // Normalize the response to handle C# PascalCase and enum mismatches
      return normalizeApiKeyTestResponse(result);
    } catch (error) {
      return classifyApiKeyTestError(error, providerConfig.providerType);
    }
  }

  // Key credential methods are delegated to the keys service
  async listKeys(...args: Parameters<FetchProvidersServiceKeys['listKeys']>) {
    return this.keysService.listKeys(...args);
  }

  async createKey(...args: Parameters<FetchProvidersServiceKeys['createKey']>) {
    return this.keysService.createKey(...args);
  }

  async updateKey(...args: Parameters<FetchProvidersServiceKeys['updateKey']>) {
    return this.keysService.updateKey(...args);
  }

  async deleteKey(...args: Parameters<FetchProvidersServiceKeys['deleteKey']>) {
    return this.keysService.deleteKey(...args);
  }

  async setPrimaryKey(...args: Parameters<FetchProvidersServiceKeys['setPrimaryKey']>) {
    return this.keysService.setPrimaryKey(...args);
  }

  async testKey(...args: Parameters<FetchProvidersServiceKeys['testKey']>) {
    return this.keysService.testKey(...args);
  }
}
