import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { components } from '@/generated/admin-api';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type { ProviderType } from '../models/providerType';

export type ProviderTool = components['schemas']['ProviderToolDto'] & Required<Pick<components['schemas']['ProviderToolDto'], 'id' | 'provider' | 'toolName' | 'isActive' | 'updatedAt'>>;
export type CreateProviderTool = components['schemas']['CreateProviderToolDto'];
export type UpdateProviderTool = components['schemas']['UpdateProviderToolDto'];
export type ToolProviderOption = components['schemas']['ToolProviderDto'] & Required<Pick<components['schemas']['ToolProviderDto'], 'value' | 'name' | 'description'>>;
export type ProviderToolImportResult = components['schemas']['ProviderToolImportResultDto'];
type ProviderToolWire = components['schemas']['ProviderToolDto'];

/**
 * Service for managing provider tools and their costs
 */
export class ProviderToolsService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Gets all provider tools
   * @param provider Optional provider type filter
   * @param isActive Optional active status filter
   */
  async getProviderTools(provider?: ProviderType, isActive?: boolean, config?: RequestConfig): Promise<ProviderTool[]> {
    const query = { provider, isActive };
    const params = new URLSearchParams();
    if (provider !== undefined) params.set('provider', String(provider));
    if (isActive !== undefined) params.set('isActive', String(isActive));
    const resolvedPath = params.size ? `/v1/admin/provider-tools?${params}` : '/v1/admin/provider-tools';
    const result = await this.client.executeContractRead(resolvedPath, (client, options) => client.GET('/v1/admin/provider-tools', { ...options, params: { query } }), config);
    return result.data.map(tool => tool as ProviderTool);
  }

  /**
   * Creates a new provider tool
   * @param tool Provider tool creation data
   */
  async createProviderTool(tool: CreateProviderTool, config?: RequestConfig): Promise<ProviderTool> {
    return this.client.executeContractOperation<ProviderToolWire, CreateProviderTool>('/v1/admin/provider-tools', HttpMethod.POST, (client, options) => client.POST('/v1/admin/provider-tools', { ...options, body: tool }), config, tool) as Promise<ProviderTool>;
  }

  /**
   * Updates an existing provider tool
   * @param id Tool ID
   * @param updates Updated tool data
   */
  async updateProviderTool(id: number, updates: UpdateProviderTool, config?: RequestConfig): Promise<ProviderTool> {
    return this.client.executeContractOperation<ProviderToolWire, UpdateProviderTool>(`/v1/admin/provider-tools/${id}`, HttpMethod.PATCH, (client, options) => client.PATCH('/v1/admin/provider-tools/{id}', { ...options, params: { path: { id } }, body: updates }), config, updates) as Promise<ProviderTool>;
  }

  /**
   * Deletes a provider tool
   * @param id Tool ID
   */
  async deleteProviderTool(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation(`/v1/admin/provider-tools/${id}`, HttpMethod.DELETE, (client, options) => client.DELETE('/v1/admin/provider-tools/{id}', { ...options, params: { path: { id } } }), config);
  }

  /**
   * Gets available provider types that support tools
   */
  async getToolProviders(config?: RequestConfig): Promise<ToolProviderOption[]> {
    const result = await this.client.executeContractRead('/v1/admin/provider-tools/providers', (client, options) => client.GET('/v1/admin/provider-tools/providers', options), config);
    return result.data.map(provider => provider as ToolProviderOption);
  }

  /**
   * Gets available billing units
   */
  async getBillingUnits(config?: RequestConfig): Promise<string[]> {
    const result = await this.client.executeContractRead('/v1/admin/provider-tools/billing-units', (client, options) => client.GET('/v1/admin/provider-tools/billing-units', options), config);
    return result.data;
  }

  /**
   * Bulk import provider tools from a JSON array
   * @param tools Array of provider tools to import
   */
  async importProviderTools(tools: CreateProviderTool[], config?: RequestConfig): Promise<ProviderToolImportResult> {
    return this.client.executeContractOperation<ProviderToolImportResult, CreateProviderTool[]>('/v1/admin/provider-tools/import', HttpMethod.POST, (client, options) => client.POST('/v1/admin/provider-tools/import', { ...options, body: tools }), config, tools);
  }

  /**
   * Exports all provider tools as JSON
   */
  async exportProviderTools(config?: RequestConfig): Promise<ProviderTool[]> {
    const result = await this.client.executeContractRead('/v1/admin/provider-tools/export', (client, options) => client.GET('/v1/admin/provider-tools/export', options), config);
    return result.data.map(tool => tool as ProviderTool);
  }
}
