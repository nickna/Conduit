import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import { HttpMethod } from '../client/HttpMethod';
import type {
  MediaRecord, MediaStorageStats, OverallMediaStorageStats, MediaCleanupRequest,
  MediaCleanupResponse, MediaDeleteResponse, MediaRestoreResponse, MediaCleanupStatus, MediaCleanupEnabledResponse,
  MediaCleanupApprovalAction,
  SimpleRetentionResponse, MediaRetentionPolicy, CreateMediaRetentionPolicyRequest,
  UpdateMediaRetentionPolicyRequest, MediaCleanupPreview,
} from '../models/media';

/** Contract-native media, cleanup, and retention operations with UI-facing adapters. */
export class FetchMediaService {
  constructor(private readonly client: FetchBaseApiClient) {}

  async getMediaByVirtualKey(id: number, includeDeleted = false, config?: RequestConfig): Promise<MediaRecord[]> {
    const suffix = includeDeleted ? '?includeDeleted=true' : '';
    const result = await this.client.executeContractRead(`/v1/admin/media-assets/virtual-key/${id}${suffix}`,
      (c, o) => c.GET('/v1/admin/media-assets/virtual-key/{virtualKeyId}', {
        ...o,
        params: { path: { virtualKeyId: id }, query: { includeDeleted } },
      }), config);
    return result.data as MediaRecord[];
  }

  async getMediaStats(type: 'overall', virtualKeyId?: never, groupId?: number, config?: RequestConfig): Promise<OverallMediaStorageStats>;
  async getMediaStats(type: 'virtual-key', virtualKeyId: number, groupId?: never, config?: RequestConfig): Promise<MediaStorageStats>;
  async getMediaStats(type: 'by-provider' | 'by-type', virtualKeyId?: never, groupId?: never, config?: RequestConfig): Promise<Record<string, number>>;
  async getMediaStats(type: 'overall' | 'virtual-key' | 'by-provider' | 'by-type' = 'overall', virtualKeyId?: number, groupId?: number, config?: RequestConfig): Promise<OverallMediaStorageStats | MediaStorageStats | Record<string, number>> {
    if (type === 'virtual-key') {
      if (!virtualKeyId) throw new Error('virtualKeyId is required for virtual-key stats');
      return this.client.executeContractRead(`/v1/admin/media-assets/stats/virtual-key/${virtualKeyId}`,
        (c, o) => c.GET('/v1/admin/media-assets/stats/virtual-key/{virtualKeyId}', { ...o, params: { path: { virtualKeyId } } }), config) as Promise<MediaStorageStats>;
    }
    if (type === 'by-provider') return this.client.executeContractRead('/v1/admin/media-assets/stats/by-provider', (c, o) => c.GET('/v1/admin/media-assets/stats/by-provider', o), config) as Promise<Record<string, number>>;
    if (type === 'by-type') return this.client.executeContractRead('/v1/admin/media-assets/stats/by-type', (c, o) => c.GET('/v1/admin/media-assets/stats/by-type', o), config) as Promise<Record<string, number>>;
    const suffix = groupId === undefined ? '' : `?virtualKeyGroupId=${groupId}`;
    return this.client.executeContractRead(`/v1/admin/media-assets/stats${suffix}`,
      (c, o) => c.GET('/v1/admin/media-assets/stats', { ...o, params: { query: { virtualKeyGroupId: groupId } } }), config) as Promise<OverallMediaStorageStats>;
  }

  async searchMedia(pattern: string, config?: RequestConfig): Promise<MediaRecord[]> {
    const result = await this.client.executeContractRead(`/v1/admin/media-assets/search?pattern=${encodeURIComponent(pattern)}`,
      (c, o) => c.GET('/v1/admin/media-assets/search', { ...o, params: { query: { pattern } } }), config);
    return result.data as MediaRecord[];
  }

  async deleteMedia(id: string, config?: RequestConfig): Promise<MediaDeleteResponse> {
    return this.client.executeContractOperation(`/v1/admin/media-assets/${encodeURIComponent(id)}`, HttpMethod.DELETE,
      (c, o) => c.DELETE('/v1/admin/media-assets/{mediaId}', { ...o, params: { path: { mediaId: id } } }), config) as Promise<MediaDeleteResponse>;
  }

  async cleanupMedia(request: MediaCleanupRequest, config?: RequestConfig): Promise<MediaCleanupResponse> {
    const force = request.force ?? false;
    if (request.type === 'expired') return this.client.executeContractOperation('/v1/admin/media-assets/cleanup/expired', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-assets/cleanup/expired', { ...o, params: { query: { force } } }), config) as Promise<MediaCleanupResponse>;
    if (request.type === 'reconciliation') return this.client.executeContractOperation('/v1/admin/media-assets/cleanup/orphaned', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-assets/cleanup/orphaned', { ...o, params: { query: { force } } }), config) as Promise<MediaCleanupResponse>;
    if (request.type !== 'prune') throw new Error('Invalid cleanup type');
    const body = { daysToKeep: request.daysToKeep, force };
    return this.client.executeContractOperation('/v1/admin/media-assets/cleanup/prune', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-assets/cleanup/prune', { ...o, body }), config, body) as Promise<MediaCleanupResponse>;
  }

  async restoreMedia(id: string, config?: RequestConfig): Promise<MediaRestoreResponse> {
    return this.client.executeContractOperation(`/v1/admin/media-assets/restore/${encodeURIComponent(id)}`, HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-assets/restore/{mediaId}', { ...o, params: { path: { mediaId: id } } }), config) as Promise<MediaRestoreResponse>;
  }

  async previewPruneMedia(daysToKeep: number, config?: RequestConfig): Promise<MediaCleanupPreview> {
    const body = { daysToKeep, force: false };
    return this.client.executeContractOperation('/v1/admin/media-assets/cleanup/prune/preview', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-assets/cleanup/prune/preview', { ...o, body }), config, body) as Promise<MediaCleanupPreview>;
  }

  async getCleanupServiceStatus(config?: RequestConfig): Promise<MediaCleanupStatus> {
    return this.client.executeContractRead('/v1/admin/media-cleanup-jobs/status', (c, o) => c.GET('/v1/admin/media-cleanup-jobs/status', o), config) as Promise<MediaCleanupStatus>;
  }
  async approveCleanup(id: string, config?: RequestConfig): Promise<MediaCleanupApprovalAction> {
    return this.client.executeContractOperation(`/v1/admin/media-cleanup-jobs/approvals/${encodeURIComponent(id)}/approve`, HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-cleanup-jobs/approvals/{approvalId}/approve', { ...o, params: { path: { approvalId: id } } }), config) as Promise<MediaCleanupApprovalAction>;
  }
  async rejectCleanup(id: string, config?: RequestConfig): Promise<MediaCleanupApprovalAction> {
    return this.client.executeContractOperation(`/v1/admin/media-cleanup-jobs/approvals/${encodeURIComponent(id)}/reject`, HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-cleanup-jobs/approvals/{approvalId}/reject', { ...o, params: { path: { approvalId: id } } }), config) as Promise<MediaCleanupApprovalAction>;
  }
  async getCleanupServiceEnabled(config?: RequestConfig): Promise<MediaCleanupEnabledResponse> {
    return this.client.executeContractRead('/v1/admin/media-cleanup-jobs/enabled', (c, o) => c.GET('/v1/admin/media-cleanup-jobs/enabled', o), config) as Promise<MediaCleanupEnabledResponse>;
  }
  async setCleanupServiceEnabled(enabled: boolean, config?: RequestConfig): Promise<MediaCleanupEnabledResponse> {
    const body = { enabled };
    return this.client.executeContractOperation('/v1/admin/media-cleanup-jobs/enabled', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-cleanup-jobs/enabled', { ...o, body }), config, body) as Promise<MediaCleanupEnabledResponse>;
  }
  async getSimpleRetentionOverride(config?: RequestConfig): Promise<SimpleRetentionResponse> {
    return this.client.executeContractRead('/v1/admin/media-cleanup-jobs/simple-retention', (c, o) => c.GET('/v1/admin/media-cleanup-jobs/simple-retention', o), config) as Promise<SimpleRetentionResponse>;
  }
  async setSimpleRetentionOverride(retentionDays: number | null, config?: RequestConfig): Promise<SimpleRetentionResponse> {
    const body = { retentionDays };
    return this.client.executeContractOperation('/v1/admin/media-cleanup-jobs/simple-retention', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-cleanup-jobs/simple-retention', { ...o, body }), config, body) as Promise<SimpleRetentionResponse>;
  }
  async getRetentionPolicies(config?: RequestConfig): Promise<MediaRetentionPolicy[]> {
    const result = await this.client.executeContractRead('/v1/admin/media-retention-policies', (c, o) => c.GET('/v1/admin/media-retention-policies', o), config);
    return result.data as MediaRetentionPolicy[];
  }
  async getRetentionPolicy(id: number, config?: RequestConfig): Promise<MediaRetentionPolicy> {
    return this.client.executeContractRead(`/v1/admin/media-retention-policies/${id}`,
      (c, o) => c.GET('/v1/admin/media-retention-policies/{id}', { ...o, params: { path: { id } } }), config) as Promise<MediaRetentionPolicy>;
  }
  async createRetentionPolicy(data: CreateMediaRetentionPolicyRequest, config?: RequestConfig): Promise<MediaRetentionPolicy> {
    return this.client.executeContractOperation('/v1/admin/media-retention-policies', HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-retention-policies', { ...o, body: data }), config, data) as Promise<MediaRetentionPolicy>;
  }
  async updateRetentionPolicy(id: number, data: UpdateMediaRetentionPolicyRequest, config?: RequestConfig): Promise<MediaRetentionPolicy> {
    return this.client.executeContractOperation(`/v1/admin/media-retention-policies/${id}`, HttpMethod.PATCH,
      (c, o) => c.PATCH('/v1/admin/media-retention-policies/{id}', { ...o, params: { path: { id } }, body: data }), config, data) as Promise<MediaRetentionPolicy>;
  }
  async deleteRetentionPolicy(id: number, config?: RequestConfig): Promise<void> {
    return this.client.executeContractOperation(`/v1/admin/media-retention-policies/${id}`, HttpMethod.DELETE,
      (c, o) => c.DELETE('/v1/admin/media-retention-policies/{id}', { ...o, params: { path: { id } } }), config);
  }
  async setDefaultRetentionPolicy(id: number, config?: RequestConfig): Promise<{ message: string }> {
    return this.client.executeContractOperation(`/v1/admin/media-retention-policies/${id}/set-default`, HttpMethod.POST,
      (c, o) => c.POST('/v1/admin/media-retention-policies/{id}/set-default', { ...o, params: { path: { id } } }), config) as Promise<{ message: string }>;
  }
}
