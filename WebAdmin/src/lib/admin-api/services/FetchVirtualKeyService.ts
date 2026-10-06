import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type { components, paths } from '@/generated/admin-api';
import {
  parseCriticalResponse,
  virtualKeyIssueSchema,
  virtualKeyValidationSchema,
} from '@/lib/api-transport/critical-response-validation';

type VirtualKeyDto = components['schemas']['VirtualKeyDto'];
type CreateVirtualKeyRequestDto = components['schemas']['CreateVirtualKeyRequestDto'];
type CreateVirtualKeyResponseDto = components['schemas']['CreateVirtualKeyResponseDto'];
type UpdateVirtualKeyRequestDto = components['schemas']['UpdateVirtualKeyRequestDto'];
type VirtualKeyValidationResponseDto = components['schemas']['VirtualKeyValidationResult'];
type VirtualKeyDiscoveryPreviewDto = components['schemas']['DiscoveryModelsResponse'];
type ListQuery = paths['/v1/admin/virtual-keys']['get']['parameters']['query'];

export interface VirtualKeyListResponseDto {
  items: VirtualKeyDto[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export class FetchVirtualKeyService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async list(page = 1, pageSize = 10, virtualKeyGroupId?: number, config?: RequestConfig): Promise<VirtualKeyListResponseDto> {
    const query: ListQuery = { page, pageSize, virtualKeyGroupId };
    const suffix = new URLSearchParams(
      Object.entries(query).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)]),
    ).toString();
    const result = await this.client.executeContractRead(`/v1/admin/virtual-keys${suffix ? `?${suffix}` : ''}`,
      (client, options) => client.GET('/v1/admin/virtual-keys', { ...options, params: { query } }), config);
    return {
      items: result.data,
      totalCount: result.pagination.totalItems,
      page: result.pagination.page,
      pageSize: result.pagination.pageSize,
      totalPages: result.pagination.totalPages,
    };
  }

  async get(id: string, config?: RequestConfig): Promise<VirtualKeyDto> {
    const numericId = Number(id);
    return this.client.executeContractRead(`/v1/admin/virtual-keys/${numericId}`,
      (client, options) => client.GET('/v1/admin/virtual-keys/{id}', { ...options, params: { path: { id: numericId } } }), config);
  }

  async create(data: CreateVirtualKeyRequestDto, config?: RequestConfig): Promise<CreateVirtualKeyResponseDto> {
    const response = await this.client.executeContractOperation('/v1/admin/virtual-keys', HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/virtual-keys', { ...options, body: data }), config, data);
    return parseCriticalResponse(virtualKeyIssueSchema, response, 'Admin virtual-key issuance') as CreateVirtualKeyResponseDto;
  }

  async update(id: string, data: UpdateVirtualKeyRequestDto, config?: RequestConfig): Promise<void> {
    const numericId = Number(id);
    await this.client.executeContractOperation(`/v1/admin/virtual-keys/${numericId}`, HttpMethod.PATCH,
      (client, options) => client.PATCH('/v1/admin/virtual-keys/{id}', {
        ...options, params: { path: { id: numericId }, header: { ['If-Match']: '*' } }, body: data,
      }), config, data);
  }

  async delete(id: string, config?: RequestConfig): Promise<void> {
    const numericId = Number(id);
    await this.client.executeContractOperation(`/v1/admin/virtual-keys/${numericId}`, HttpMethod.DELETE,
      (client, options) => client.DELETE('/v1/admin/virtual-keys/{id}', {
        ...options, params: { path: { id: numericId }, header: { ['If-Match']: '*' } },
      }), config);
  }

  async validate(key: string, config?: RequestConfig): Promise<VirtualKeyValidationResponseDto> {
    const body: components['schemas']['ValidateVirtualKeyRequest'] = { key };
    const response = await this.client.executeContractOperation('/v1/admin/virtual-keys/validate', HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/virtual-keys/validate', { ...options, body }), config, body);
    return parseCriticalResponse(virtualKeyValidationSchema, response, 'Admin virtual-key validation');
  }

  async previewDiscovery(id: string, capability?: string, config?: RequestConfig): Promise<VirtualKeyDiscoveryPreviewDto> {
    const numericId = Number(id);
    const suffix = capability ? `?capability=${encodeURIComponent(capability)}` : '';
    return this.client.executeContractRead(`/v1/admin/virtual-keys/${numericId}/discovery-preview${suffix}`,
      (client, options) => client.GET('/v1/admin/virtual-keys/{id}/discovery-preview', {
        ...options, params: { path: { id: numericId }, query: { capability } },
      }), config);
  }
}
