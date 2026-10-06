import { FilterOptions } from './common';
import { ProviderType } from './providerType';
import type { components } from '@/generated/admin-api';

export type ProviderDto = components['schemas']['ProviderDto'];
export type CreateProviderDto = components['schemas']['CreateProviderRequest'];
export type UpdateProviderDto = components['schemas']['UpdateProviderRequest'];


export interface ProviderConnectionTestRequest {
  providerType: ProviderType;
  apiKey?: string;
  baseUrl?: string | null;
  organization?: string | null;
}

export interface ProviderConnectionTestResultDto {
  success: boolean;
  message: string;
  errorDetails?: string;
  providerType: ProviderType;
  modelsAvailable?: string[];
  responseTimeMs?: number;
  timestamp?: string;
}

export interface ProviderFilters extends FilterOptions {
  isEnabled?: boolean;
  providerType?: ProviderType;
  hasApiKey?: boolean;
}

export interface ProviderUsageStatistics {
  providerType: ProviderType;
  totalRequests: number;
  successfulRequests: number;
  failedRequests: number;
  averageResponseTime: number;
  totalCost: number;
  modelsUsed: Record<string, number>;
  errorTypes: Record<string, number>;
  timeRange: {
    start: string;
    end: string;
  };
}

// Provider Key Credential interfaces
export type ProviderKeyCredentialDto = components['schemas']['ProviderKeyCredentialDto'];
export type CreateProviderKeyCredentialDto = components['schemas']['CreateKeyRequest'];
export type UpdateProviderKeyCredentialDto = components['schemas']['UpdateKeyRequest'];

export interface ProviderKeyRotationDto {
  newApiKey: string;
  organization?: string;
  keyName?: string;
}

// API Key Test Response Types.
// Mirrors ConduitLLM.Configuration.DTOs.ApiKeyTestResult; every member the backend can return must
// appear here, or a classified result degrades into UNKNOWN_ERROR on the way through the client.
export enum ApiKeyTestResult {
  SUCCESS = 'success',
  INVALID_KEY = 'invalid_key',
  IGNORED = 'ignored',
  PROVIDER_DOWN = 'provider_down',
  RATE_LIMITED = 'rate_limited',
  UNKNOWN_ERROR = 'unknown_error',
  /**
   * The provider is misconfigured - a required structured setting is missing, or the endpoint
   * rejected the request outright - so the key itself was never actually exercised.
   */
  CONFIGURATION = 'configuration'
}

export interface StandardApiKeyTestResponse {
  result: ApiKeyTestResult;
  message: string;
  details?: {
    responseTimeMs?: number;
    modelsAvailable?: string[];
    providerMessage?: string; // Raw provider message for debugging
    errorCode?: string;
    statusCode?: number;
  };
}

// Lightweight DTO for referencing providers without exposing sensitive data
export type ProviderReferenceDto = components['schemas']['ProviderReferenceDto'];
