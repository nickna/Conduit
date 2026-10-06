import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { components } from '@/generated/admin-api';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import {
  type FunctionConfigurationDto, type CreateFunctionConfigurationDto, type UpdateFunctionConfigurationDto,
  type FunctionCredentialDto, type CreateFunctionCredentialDto, type UpdateFunctionCredentialDto,
  type TestCredentialRequestDto, type TestCredentialResponseDto, type FunctionCostDto,
  type CreateFunctionCostDto, type UpdateFunctionCostDto, type FunctionExecutionDto,
  FunctionProviderType, ExecutionState,
} from '../models/functions';
import { ValidationError } from '../utils/errors';
import { validateRequired, validateStringLength } from '../utils/validation';

type ConfigurationWire = components['schemas']['FunctionConfigurationDto'];
type CreateConfigurationWire = components['schemas']['CreateFunctionConfigurationRequest'];
type UpdateConfigurationWire = components['schemas']['UpdateFunctionConfigurationRequest'];
type CredentialWire = components['schemas']['FunctionCredentialDto'];
type CreateCredentialWire = components['schemas']['CreateFunctionCredentialRequest'];
type UpdateCredentialWire = components['schemas']['UpdateFunctionCredentialRequest'];
type CostWire = components['schemas']['FunctionCostDto'];
type CreateCostWire = components['schemas']['CreateFunctionCostDto'];
type UpdateCostWire = components['schemas']['UpdateFunctionCostDto'];
type ExecutionWire = components['schemas']['AdminFunctionExecutionDto'];

const stringifyStructuredJson = (value: Record<string, unknown> | null | undefined): string | null =>
  value === null || value === undefined ? null : JSON.stringify(value);

const parseStructuredJson = (value: string | null | undefined, field: string): Record<string, unknown> | undefined => {
  if (value === null || value === undefined || value.trim() === '') return undefined;
  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      throw new Error('must be an object');
    }
    return parsed as Record<string, unknown>;
  } catch {
    throw new ValidationError(`${field} must be a valid JSON object`);
  }
};

const configurationFromWire = (value: ConfigurationWire): FunctionConfigurationDto => ({
  ...value,
  providerSettings: stringifyStructuredJson(value.providerSettings),
  parameterSchema: stringifyStructuredJson(value.parameterSchema),
}) as unknown as FunctionConfigurationDto;
const credentialFromWire = (value: CredentialWire): FunctionCredentialDto => value as unknown as FunctionCredentialDto;
const executionFromWire = (value: ExecutionWire): FunctionExecutionDto => ({
  id: value.id ?? '',
  functionId: value.functionId ?? 0,
  status: value.status ?? ExecutionState.Pending,
  input: value.input ?? undefined,
  output: value.output ?? undefined,
  error: value.error ?? undefined,
  createdAt: value.createdAt ?? '',
  startedAt: value.startedAt ?? undefined,
  completedAt: value.completedAt ?? undefined,
  durationMs: value.durationMs ?? undefined,
  cost: {
    estimated: value.cost?.estimated ?? undefined,
    actual: value.cost?.actual ?? undefined,
    currency: value.cost?.currency ?? 'USD',
    breakdown: value.cost?.breakdown ?? undefined,
  },
  admin: {
    virtualKeyId: value.admin?.virtualKeyId ?? 0,
    executionMode: value.admin?.executionMode ?? 'synchronous',
    retryCount: value.admin?.retryCount ?? 0,
    nextRetryAt: value.admin?.nextRetryAt,
    leasedBy: value.admin?.leasedBy,
    leaseExpiresAt: value.admin?.leaseExpiresAt,
    version: value.admin?.version ?? 0,
    webhookUrl: value.admin?.webhookUrl,
    webhookDelivered: value.admin?.webhookDelivered ?? false,
    progressPercentage: value.admin?.progressPercentage,
    statusMessage: value.admin?.statusMessage,
  },
});
const costFromWire = (value: CostWire): FunctionCostDto => ({
  ...value,
  baseCost: value.baseCost ?? undefined,
  pricingConfiguration: JSON.stringify(value.pricingConfiguration ?? {}),
  expiryDate: value.expiryDate ?? undefined,
  description: value.description ?? undefined,
}) as unknown as FunctionCostDto;

function validateCreateConfiguration(data: CreateFunctionConfigurationDto): void {
  validateRequired(data, ['configurationName', 'providerType', 'purpose']);
  validateStringLength(data.configurationName, 1, 255, 'configurationName');
  if (data.timeoutSeconds !== undefined && data.timeoutSeconds < 1) throw new ValidationError('timeoutSeconds must be at least 1');
}
function validateCreateCredential(data: CreateFunctionCredentialDto): void {
  validateRequired(data, ['providerType', 'keyName', 'apiKey']);
  validateStringLength(data.keyName, 1, 255, 'keyName');
  validateStringLength(data.apiKey, 1, 1000, 'apiKey');
}
function validateCreateCost(data: CreateFunctionCostDto): void {
  validateRequired(data, ['costName', 'providerType', 'pricingModel', 'pricingConfiguration']);
  validateStringLength(data.costName, 1, 255, 'costName');
  parseStructuredJson(data.pricingConfiguration, 'pricingConfiguration');
}

export class FetchFunctionConfigurationsService {
  constructor(private readonly client: FetchBaseApiClient) {}
  async list(config?: RequestConfig): Promise<FunctionConfigurationDto[]> {
    const data = await this.client.executeContractRead('/v1/admin/function-configurations', (c, o) => c.GET('/v1/admin/function-configurations', o), config);
    return data.data.map(configurationFromWire);
  }
  async getById(id: number, config?: RequestConfig): Promise<FunctionConfigurationDto> {
    const data = await this.client.executeContractRead(`/v1/admin/function-configurations/${id}`, (c, o) => c.GET('/v1/admin/function-configurations/{id}', { ...o, params: { path: { id } } }), config);
    return configurationFromWire(data);
  }
  async getByProvider(providerType: FunctionProviderType, config?: RequestConfig): Promise<FunctionConfigurationDto[]> {
    const provider = providerType;
    const data = await this.client.executeContractRead(`/v1/admin/function-configurations/provider/${encodeURIComponent(provider)}`, (c, o) => c.GET('/v1/admin/function-configurations/provider/{providerType}', { ...o, params: { path: { providerType: provider } } }), config);
    return data.data.map(configurationFromWire);
  }
  async create(data: CreateFunctionConfigurationDto, config?: RequestConfig): Promise<FunctionConfigurationDto> {
    validateCreateConfiguration(data);
    const body = {
      ...data,
      providerSettings: parseStructuredJson(data.providerSettings, 'providerSettings'),
      parameterSchema: parseStructuredJson(data.parameterSchema, 'parameterSchema'),
    } as unknown as CreateConfigurationWire;
    const result = await this.client.executeContractOperation<ConfigurationWire, CreateConfigurationWire>('/v1/admin/function-configurations', HttpMethod.POST, (c, o) => c.POST('/v1/admin/function-configurations', { ...o, body }), config, body);
    return configurationFromWire(result);
  }
  async update(id: number, data: UpdateFunctionConfigurationDto, config?: RequestConfig): Promise<FunctionConfigurationDto> {
    const body = {
      ...data,
      providerSettings: parseStructuredJson(data.providerSettings, 'providerSettings'),
      parameterSchema: parseStructuredJson(data.parameterSchema, 'parameterSchema'),
    } as unknown as UpdateConfigurationWire;
    const result = await this.client.executeContractOperation<ConfigurationWire, UpdateConfigurationWire>(`/v1/admin/function-configurations/${id}`, HttpMethod.PATCH, (c, o) => c.PATCH('/v1/admin/function-configurations/{id}', { ...o, params: { path: { id } }, body }), config, body);
    return configurationFromWire(result);
  }
  async deleteById(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation(`/v1/admin/function-configurations/${id}`, HttpMethod.DELETE, (c, o) => c.DELETE('/v1/admin/function-configurations/{id}', { ...o, params: { path: { id } } }), config);
  }
}

export class FetchFunctionCredentialsService {
  constructor(private readonly client: FetchBaseApiClient) {}
  async list(config?: RequestConfig): Promise<FunctionCredentialDto[]> { const data = await this.client.executeContractRead('/v1/admin/function-credentials', (c, o) => c.GET('/v1/admin/function-credentials', o), config); return data.data.map(credentialFromWire); }
  async getById(id: number, config?: RequestConfig): Promise<FunctionCredentialDto> { const data = await this.client.executeContractRead(`/v1/admin/function-credentials/${id}`, (c, o) => c.GET('/v1/admin/function-credentials/{id}', { ...o, params: { path: { id } } }), config); return credentialFromWire(data); }
  async getByConfiguration(functionConfigurationId: number, config?: RequestConfig): Promise<FunctionCredentialDto[]> { const data = await this.client.executeContractRead(`/v1/admin/function-credentials/configuration/${functionConfigurationId}`, (c, o) => c.GET('/v1/admin/function-credentials/configuration/{functionConfigurationId}', { ...o, params: { path: { functionConfigurationId } } }), config); return data.data.map(credentialFromWire); }
  async create(data: CreateFunctionCredentialDto, config?: RequestConfig): Promise<FunctionCredentialDto> { validateCreateCredential(data); const body = data as unknown as CreateCredentialWire; const result = await this.client.executeContractOperation<CredentialWire, CreateCredentialWire>('/v1/admin/function-credentials', HttpMethod.POST, (c, o) => c.POST('/v1/admin/function-credentials', { ...o, body }), config, body); return credentialFromWire(result); }
  async update(id: number, data: UpdateFunctionCredentialDto, config?: RequestConfig): Promise<FunctionCredentialDto> { const body = data as unknown as UpdateCredentialWire; const result = await this.client.executeContractOperation<CredentialWire, UpdateCredentialWire>(`/v1/admin/function-credentials/${id}`, HttpMethod.PATCH, (c, o) => c.PATCH('/v1/admin/function-credentials/{id}', { ...o, params: { path: { id } }, body }), config, body); return credentialFromWire(result); }
  async deleteById(id: number, config?: RequestConfig): Promise<void> { return this.client.executeContractOperation(`/v1/admin/function-credentials/${id}`, HttpMethod.DELETE, (c, o) => c.DELETE('/v1/admin/function-credentials/{id}', { ...o, params: { path: { id } } }), config); }
  async testCredential(data: TestCredentialRequestDto, config?: RequestConfig): Promise<TestCredentialResponseDto> { return this.client.executeContractOperation('/v1/admin/function-credentials/test', HttpMethod.POST, (c, o) => c.POST('/v1/admin/function-credentials/test', { ...o, body: data }), config, data) as Promise<TestCredentialResponseDto>; }
}

export class FetchFunctionCostsService {
  constructor(private readonly client: FetchBaseApiClient) {}
  async list(config?: RequestConfig): Promise<FunctionCostDto[]> { const data = await this.client.executeContractRead('/v1/admin/function-costs', (c, o) => c.GET('/v1/admin/function-costs', o), config); return data.data.map(costFromWire); }
  async getById(id: number, config?: RequestConfig): Promise<FunctionCostDto> { const data = await this.client.executeContractRead(`/v1/admin/function-costs/${id}`, (c, o) => c.GET('/v1/admin/function-costs/{id}', { ...o, params: { path: { id } } }), config); return costFromWire(data); }
  async getByConfiguration(functionConfigurationId: number, config?: RequestConfig): Promise<FunctionCostDto> { const data = await this.client.executeContractRead(`/v1/admin/function-costs/configuration/${functionConfigurationId}`, (c, o) => c.GET('/v1/admin/function-costs/configuration/{functionConfigurationId}', { ...o, params: { path: { functionConfigurationId } } }), config); return costFromWire(data); }
  async create(data: CreateFunctionCostDto, config?: RequestConfig): Promise<FunctionCostDto> { validateCreateCost(data); const body = { ...data, pricingConfiguration: parseStructuredJson(data.pricingConfiguration, 'pricingConfiguration') } as unknown as CreateCostWire; const result = await this.client.executeContractOperation<CostWire, CreateCostWire>('/v1/admin/function-costs', HttpMethod.POST, (c, o) => c.POST('/v1/admin/function-costs', { ...o, body }), config, body); return costFromWire(result); }
  async update(id: number, data: UpdateFunctionCostDto, config?: RequestConfig): Promise<FunctionCostDto> { const body = { ...data, pricingConfiguration: parseStructuredJson(data.pricingConfiguration, 'pricingConfiguration') } as unknown as UpdateCostWire; const result = await this.client.executeContractOperation<CostWire, UpdateCostWire>(`/v1/admin/function-costs/${id}`, HttpMethod.PATCH, (c, o) => c.PATCH('/v1/admin/function-costs/{id}', { ...o, params: { path: { id } }, body }), config, body); return costFromWire(result); }
  async deleteById(id: number, config?: RequestConfig): Promise<void> { return this.client.executeContractOperation(`/v1/admin/function-costs/${id}`, HttpMethod.DELETE, (c, o) => c.DELETE('/v1/admin/function-costs/{id}', { ...o, params: { path: { id } } }), config); }
  async clearCache(config?: RequestConfig): Promise<{ message: string }> { return this.client.executeContractOperation('/v1/admin/function-costs/cache/clear', HttpMethod.POST, (c, o) => c.POST('/v1/admin/function-costs/cache/clear', o), config); }
}

export class FetchFunctionExecutionsService {
  constructor(private readonly client: FetchBaseApiClient) {}
  async getById(id: string, config?: RequestConfig): Promise<FunctionExecutionDto> { const data = await this.client.executeContractRead(`/v1/admin/function-executions/${encodeURIComponent(id)}`, (c, o) => c.GET('/v1/admin/function-executions/{id}', { ...o, params: { path: { id } } }), config); return executionFromWire(data); }
  async getByConfiguration(functionConfigurationId: number, config?: RequestConfig): Promise<FunctionExecutionDto[]> { const data = await this.client.executeContractRead(`/v1/admin/function-executions/configuration/${functionConfigurationId}`, (c, o) => c.GET('/v1/admin/function-executions/configuration/{functionConfigurationId}', { ...o, params: { path: { functionConfigurationId } } }), config); return data.data.map(executionFromWire); }
  async getByState(state: ExecutionState, config?: RequestConfig): Promise<FunctionExecutionDto[]> { const value = state; const data = await this.client.executeContractRead(`/v1/admin/function-executions/state/${encodeURIComponent(value)}`, (c, o) => c.GET('/v1/admin/function-executions/state/{state}', { ...o, params: { path: { state: value } } }), config); return data.data.map(executionFromWire); }
  async cleanup(olderThanDays = 30, config?: RequestConfig): Promise<{ deletedCount: number; message: string }> { return this.client.executeContractOperation(`/v1/admin/function-executions/cleanup?olderThanDays=${olderThanDays}`, HttpMethod.DELETE, (c, o) => c.DELETE('/v1/admin/function-executions/cleanup', { ...o, params: { query: { olderThanDays } } }), config) as Promise<{ deletedCount: number; message: string }>; }
}
