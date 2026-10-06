import { FilterOptions } from './common';
import { VirtualKeyMetadata } from './metadata';
import type { components } from '@/generated/admin-api';

export const TransactionType = {
  Credit: 'credit',
  Debit: 'debit',
  Refund: 'refund',
  Adjustment: 'adjustment',
} as const;
export type TransactionType = (typeof TransactionType)[keyof typeof TransactionType];

export const ReferenceType = {
  Manual: 'manual',
  VirtualKey: 'virtualKey',
  System: 'system',
  Initial: 'initial',
} as const;
export type ReferenceType = (typeof ReferenceType)[keyof typeof ReferenceType];

export type VirtualKeyGroupDto = components['schemas']['VirtualKeyGroupDto'];
export type CreateVirtualKeyGroupRequestDto = components['schemas']['CreateVirtualKeyGroupRequestDto'];
export type UpdateVirtualKeyGroupRequestDto = components['schemas']['UpdateVirtualKeyGroupRequestDto'];
export type AdjustBalanceDto = components['schemas']['AdjustBalanceDto'];
export type VirtualKeyGroupTransactionDto = components['schemas']['VirtualKeyGroupTransactionDto'];

export interface TransactionHistoryParams {
  page?: number;
  pageSize?: number;
}

export type VirtualKeyDto = components['schemas']['VirtualKeyDto'];
export type CreateVirtualKeyRequest = components['schemas']['CreateVirtualKeyRequestDto'];
export type CreateVirtualKeyResponse = components['schemas']['CreateVirtualKeyResponseDto'];
export type UpdateVirtualKeyRequest = components['schemas']['UpdateVirtualKeyRequestDto'];
export type VirtualKeyValidationRequest = components['schemas']['ValidateVirtualKeyRequest'];
export type VirtualKeyValidationResult = components['schemas']['VirtualKeyValidationResult'];

// Note: Spend tracking is now handled at the VirtualKeyGroup level

export interface VirtualKeyValidationInfo {
  keyId: number;
  keyName: string;
  isValid: boolean;
  validationErrors: string[];
  allowedModels: string[];
  virtualKeyGroupId: number;
  rateLimits?: {
    rpm?: number;
    rpd?: number;
  };
  metadata?: VirtualKeyMetadata;
}

export interface VirtualKeyMaintenanceRequest {
  cleanupExpiredKeys?: boolean;
}

export interface VirtualKeyMaintenanceResponse {
  expiredKeysDeleted?: number;
  errors?: string[];
}

export interface VirtualKeyFilters extends FilterOptions {
  isEnabled?: boolean;
  hasExpired?: boolean;
  allowedModels?: string[];
  createdAfter?: string;
  createdBefore?: string;
  virtualKeyGroupId?: number;
}

export interface VirtualKeyStatistics {
  totalKeys: number;
  activeKeys: number;
  expiredKeys: number;
  totalGroups: number;
  keysByGroup: Record<number, number>;
}
