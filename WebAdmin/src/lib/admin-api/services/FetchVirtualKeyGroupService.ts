import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type { components, paths } from '@/generated/admin-api';
import type { TransactionHistoryParams } from '../models/virtualKey';
import { balanceAdjustmentSchema, parseCriticalResponse } from '@/lib/api-transport/critical-response-validation';

type VirtualKeyGroupDto = components['schemas']['VirtualKeyGroupDto'];
type CreateVirtualKeyGroupRequestDto = components['schemas']['CreateVirtualKeyGroupRequestDto'];
type UpdateVirtualKeyGroupRequestDto = components['schemas']['UpdateVirtualKeyGroupRequestDto'];
type AdjustBalanceDto = components['schemas']['AdjustBalanceDto'];
type VirtualKeyDto = components['schemas']['VirtualKeyDto'];
type PagedGroups = components['schemas']['PagedResultOfVirtualKeyGroupDto'];
type PagedTransactions = components['schemas']['PagedResultOfVirtualKeyGroupTransactionDto'];
type ListQuery = paths['/v1/admin/virtual-key-groups']['get']['parameters']['query'];

export interface ListGroupsParams { page?: number; pageSize?: number }

export class FetchVirtualKeyGroupService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async list(params?: ListGroupsParams, config?: RequestConfig): Promise<PagedGroups> {
    const query: ListQuery = params;
    const qs = new URLSearchParams(Object.entries(params ?? {}).map(([k, v]) => [k, String(v)])).toString();
    return this.client.executeContractRead(`/v1/admin/virtual-key-groups${qs ? `?${qs}` : ''}`,
      (client, options) => client.GET('/v1/admin/virtual-key-groups', { ...options, params: { query } }), config);
  }

  async get(id: number, config?: RequestConfig): Promise<VirtualKeyGroupDto> {
    return this.client.executeContractRead(`/v1/admin/virtual-key-groups/${id}`,
      (client, options) => client.GET('/v1/admin/virtual-key-groups/{id}', { ...options, params: { path: { id } } }), config);
  }

  async create(data: CreateVirtualKeyGroupRequestDto, config?: RequestConfig): Promise<VirtualKeyGroupDto> {
    return this.client.executeContractOperation('/v1/admin/virtual-key-groups', HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/virtual-key-groups', { ...options, body: data }), config, data);
  }

  async update(id: number, data: UpdateVirtualKeyGroupRequestDto, config?: RequestConfig): Promise<void> {
    await this.client.executeContractOperation(`/v1/admin/virtual-key-groups/${id}`, HttpMethod.PATCH,
      (client, options) => client.PATCH('/v1/admin/virtual-key-groups/{id}', {
        ...options, params: { path: { id }, header: { ['If-Match']: '*' } }, body: data,
      }), config, data);
  }

  async adjustBalance(id: number, data: AdjustBalanceDto, config?: RequestConfig): Promise<VirtualKeyGroupDto> {
    const response = await this.client.executeContractOperation(`/v1/admin/virtual-key-groups/${id}/adjust-balance`, HttpMethod.POST,
      (client, options) => client.POST('/v1/admin/virtual-key-groups/{id}/adjust-balance', {
        ...options, params: { path: { id }, header: { ['If-Match']: '*' } }, body: data,
      }), config, data);
    return parseCriticalResponse(balanceAdjustmentSchema, response, 'Admin virtual-key-group balance adjustment') as VirtualKeyGroupDto;
  }

  async delete(id: number, config?: RequestConfig): Promise<void> {
    await this.client.executeContractOperation(`/v1/admin/virtual-key-groups/${id}`, HttpMethod.DELETE,
      (client, options) => client.DELETE('/v1/admin/virtual-key-groups/{id}', {
        ...options, params: { path: { id }, header: { ['If-Match']: '*' } },
      }), config);
  }

  async getKeys(id: number, config?: RequestConfig): Promise<VirtualKeyDto[]> {
    const result = await this.client.executeContractRead(`/v1/admin/virtual-key-groups/${id}/keys`,
      (client, options) => client.GET('/v1/admin/virtual-key-groups/{id}/keys', { ...options, params: { path: { id } } }), config);
    return result.data;
  }

  async getTransactionHistory(id: number, params?: TransactionHistoryParams, config?: RequestConfig): Promise<PagedTransactions> {
    const query = params;
    const qs = new URLSearchParams(Object.entries(params ?? {}).map(([k, v]) => [k, String(v)])).toString();
    return this.client.executeContractRead(`/v1/admin/virtual-key-groups/${id}/transactions${qs ? `?${qs}` : ''}`,
      (client, options) => client.GET('/v1/admin/virtual-key-groups/{id}/transactions', {
        ...options, params: { path: { id }, query },
      }), config);
  }
}
