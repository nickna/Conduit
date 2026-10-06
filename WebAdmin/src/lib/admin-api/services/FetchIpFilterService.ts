import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import { HttpMethod } from '../client/HttpMethod';
import type { components, paths } from '@/generated/admin-api';
import type {
  IpFilterDto,
  CreateIpFilterDto,
  UpdateIpFilterDto,
  IpFilterSettingsDto,
  UpdateIpFilterSettingsDto,
  IpCheckResult,
} from '../models/ipFilter';
import { ValidationError } from '../utils/errors';
import { validateRequired, validateStringLength, validateEnum } from '../utils/validation';
import { getIpValidationError, getIpOrCidrValidationError } from '@/lib/utils/ip-validation';

const CACHE_TTL_SECONDS = 60;
type IpFilterWire = components['schemas']['IpFilterDto'];

function toIpFilterDto(filter: IpFilterWire): IpFilterDto {
  if (filter.filterType !== 'whitelist' && filter.filterType !== 'blacklist') {
    throw new Error(`Unsupported IP filter type: ${filter.filterType}`);
  }
  return {
    id: filter.id,
    name: filter.name,
    ipAddressOrCidr: filter.ipAddressOrCidr,
    filterType: filter.filterType,
    isEnabled: filter.isEnabled,
    description: filter.description ?? undefined,
    createdAt: filter.createdAt,
    updatedAt: filter.updatedAt,
    createdBy: filter.createdBy ?? undefined,
    updatedBy: filter.updatedBy ?? undefined,
    virtualKeyId: filter.virtualKeyId,
  };
}

function validateIpAddressOrCidr(value: string): void {
  const error = getIpOrCidrValidationError(value);
  if (error) throw new ValidationError(error);
}

function validateIpAddress(value: string): void {
  const error = getIpValidationError(value);
  if (error) throw new ValidationError(error);
}

function validateCreateIpFilterRequest(request: CreateIpFilterDto): void {
  validateRequired(request, ['name', 'ipAddressOrCidr', 'filterType']);
  validateStringLength(request.name, 1, 100, 'name');
  validateIpAddressOrCidr(request.ipAddressOrCidr);
  validateEnum(request.filterType, ['whitelist', 'blacklist'] as const, 'filterType');
  if (request.description) validateStringLength(request.description, 0, 500, 'description');
}

export class FetchIpFilterService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async create(request: CreateIpFilterDto): Promise<IpFilterDto> {
    validateCreateIpFilterRequest(request);
    const body: components['schemas']['CreateIpFilterDto'] = request;
    const response = await this.client.executeContractOperation('/v1/admin/ip-filters', HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/ip-filters', { ...options, body }), undefined, body);
    await this.invalidateCache();
    return toIpFilterDto(response);
  }

  async list(): Promise<IpFilterDto[]> {
    const cacheKey = this.client.getCacheKey('ip-filters');
    return this.client.withCache(cacheKey, async () => {
      const pageSize = 100;
      const readPage = (page: number) => this.client.executeContractRead(
        `/v1/admin/ip-filters?page=${page}&pageSize=${pageSize}`,
        (client, options) => client.GET('/v1/admin/ip-filters', {
          ...options,
          params: { query: { page, pageSize } },
        }),
      );
      const firstPage = await readPage(1);
      const filters = [...firstPage.data];
      for (let page = 2; page <= firstPage.pagination.totalPages; page += 1) {
        filters.push(...(await readPage(page)).data);
      }
      return filters.map(toIpFilterDto);
    }, CACHE_TTL_SECONDS);
  }

  async getById(id: number): Promise<IpFilterDto> {
    const cacheKey = this.client.getCacheKey('ip-filter', id);
    return this.client.withCache(cacheKey, async () => toIpFilterDto(await this.client.executeContractRead(`/v1/admin/ip-filters/${id}`,
      (client, options) => client.GET('/v1/admin/ip-filters/{id}', { ...options, params: { path: { id } } }))), CACHE_TTL_SECONDS);
  }

  async getEnabled(): Promise<IpFilterDto[]> {
    return this.client.withCache('ip-filters-enabled', async () => {
      const result = await this.client.executeContractRead('/v1/admin/ip-filters/enabled',
        (client, options) => client.GET('/v1/admin/ip-filters/enabled', options));
      return result.data.map(toIpFilterDto);
    }, CACHE_TTL_SECONDS);
  }

  async listByVirtualKey(virtualKeyId: number): Promise<IpFilterDto[]> {
    const cacheKey = this.client.getCacheKey('ip-filters-by-vkey', virtualKeyId);
    return this.client.withCache(cacheKey, async () => {
      const result = await this.client.executeContractRead(`/v1/admin/ip-filters/by-virtual-key/${virtualKeyId}`,
      (client, options) => client.GET('/v1/admin/ip-filters/by-virtual-key/{virtualKeyId}', {
        ...options, params: { path: { virtualKeyId } },
      }));
      return result.data.map(toIpFilterDto);
    }, CACHE_TTL_SECONDS);
  }

  async update(id: number, request: UpdateIpFilterDto): Promise<void> {
    const body = request as unknown as components['schemas']['UpdateIpFilterDto'];
    await this.client.executeContractOperation(`/v1/admin/ip-filters/${id}`, HttpMethod.PATCH,
      (client, options) => client.PATCH('/v1/admin/ip-filters/{id}', {
        ...options, params: { path: { id }, header: { ['If-Match']: '*' } }, body,
      }), undefined, body);
    await this.invalidateCache();
  }

  async deleteById(id: number): Promise<void> {
    await this.client.executeContractOperation(`/v1/admin/ip-filters/${id}`, HttpMethod.DELETE,
      (client, options) => client.DELETE('/v1/admin/ip-filters/{id}', {
        ...options, params: { path: { id }, header: { ['If-Match']: '*' } },
      }));
    await this.invalidateCache();
  }

  async getSettings(): Promise<IpFilterSettingsDto> {
    return this.client.withCache('ip-filter-settings', () => this.client.executeContractRead('/v1/admin/ip-filters/settings',
      (client, options) => client.GET('/v1/admin/ip-filters/settings', options)) as Promise<IpFilterSettingsDto>, CACHE_TTL_SECONDS);
  }

  async updateSettings(request: UpdateIpFilterSettingsDto): Promise<void> {
    type SettingsBody = paths['/v1/admin/ip-filters/settings']['put']['requestBody']['content']['application/json'];
    // Preserve the partial facade until the backend stops binding the full settings response model.
    const body = request as SettingsBody;
    await this.client.executeContractOperation('/v1/admin/ip-filters/settings', HttpMethod.PUT,
      (client, options) => client.PUT('/v1/admin/ip-filters/settings', { ...options, body }), undefined, body);
    await this.invalidateCache();
  }

  async checkIp(ipAddress: string): Promise<IpCheckResult> {
    validateIpAddress(ipAddress);
    return this.client.executeContractRead(`/v1/admin/ip-filters/check/${encodeURIComponent(ipAddress)}`,
      (client, options) => client.GET('/v1/admin/ip-filters/check/{ipAddress}', {
        ...options, params: { path: { ipAddress } },
      })) as Promise<IpCheckResult>;
  }

  async enableFilter(id: number): Promise<void> { await this.update(id, { isEnabled: true }); }
  async disableFilter(id: number): Promise<void> { await this.update(id, { isEnabled: false }); }

  private async invalidateCache(): Promise<void> {
    await this.client.clearCache();
  }
}
