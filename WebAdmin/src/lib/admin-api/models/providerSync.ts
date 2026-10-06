import type { components } from '@/generated/admin-api';

export type DriftItemDto = components['schemas']['DriftItemDto'];
export type ProviderSyncRunDto = components['schemas']['ProviderSyncRunDto'];
export type DriftActionResultDto = components['schemas']['DriftActionResultDto'];
export type BulkDriftActionRequest = components['schemas']['BulkDriftActionRequest'];
export type BulkDriftActionResponse = components['schemas']['BulkDriftActionResponse'];

export interface DriftItemFilter {
  status?: string;
  driftType?: string;
  providerId?: number;
  page?: number;
  pageSize?: number;
}
