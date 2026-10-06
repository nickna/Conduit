'use client';

import { useQuery } from '@tanstack/react-query';
import { withAdminClient } from '@/lib/client/adminClient';
import type { components } from '@/generated/admin-api';

type LogRequestDto = components['schemas']['LogRequestDto'];

/**
 * Request log entry as returned by the backend API
 * This matches the C# LogRequestDto structure
 */
export interface RequestLogEntry {
  id: number;
  virtualKeyId: number;
  modelName: string;
  providerId: number | null;
  providerType: string | null;
  modelProviderMappingId: number | null;
  promptCachingEligible: boolean;
  promptCachingPolicyApplied: boolean;
  cachedReadSavings: number;
  cacheWritePremium: number;
  routingAffinityUsed: boolean;
  routingDecisionReason: string | null;
  routingFailoverCount: number;
  requestType: string;
  inputTokens: number;
  outputTokens: number;
  cachedInputTokens: number | null;
  cachedWriteTokens: number | null;
  cost: number;
  billingMethod: components['schemas']['RequestBillingMethod'];
  providerReportedCostUsd: number | null;
  providerCostMarkupMultiplier: number | null;
  billedAtUtc: string | null;
  responseTimeMs: number;
  userId: string | null;
  clientIp: string | null;
  requestPath: string | null;
  statusCode: number | null;
  timestamp: string;
  metadata: RequestLogMetadata | null;
}

/**
 * Tool call item in chat completion responses (basic format)
 */
export interface ChatToolCallItem {
  id?: string;
  type?: string;
  functionName?: string;
  hasArguments?: boolean;
}

/**
 * Function execution result with rich metadata (for agentic chat completions)
 */
export interface FunctionCallResult {
  toolCallId?: string;
  functionName?: string;
  status?: 'completed' | 'failed';
  cost?: number;
  errorMessage?: string;
  functionExecutionId?: string;
}

/**
 * Parsed metadata for different request types
 */
export interface RequestLogMetadata {
  [key: string]: unknown;
  type?: string;
  // Function-specific (for /functions/execute endpoint)
  functionConfigurationId?: number;
  functionName?: string;
  executionId?: string;
  state?: string;
  errorMessage?: string;
  // Chat tool calls (for chat completions with tools - basic format)
  toolCallCount?: number;
  toolCalls?: ChatToolCallItem[];
  // Chat function executions (for agentic chat completions - rich format)
  functionCallCount?: number;
  totalCost?: number;
  successCount?: number;
  failedCount?: number;
  functionCalls?: FunctionCallResult[];
  // Image-specific
  size?: string;
  quality?: string;
  style?: string;
  imageCount?: number;
  // Video-specific
  duration?: number;
  resolution?: string;
  aspectRatio?: string;
  // Audio-specific
  voice?: string;
  format?: string;
  durationSeconds?: number;
}

/**
 * Paginated response from the backend
 */
/**
 * Filter options for request logs
 */
export interface RequestLogFormFilters {
  startDate?: Date;
  endDate?: Date;
  model?: string;
  virtualKeyId?: number;
  status?: number;
}

/**
 * Statistics calculated from request logs
 */
export interface RequestLogStats {
  totalRequests: number;
  successCount: number;
  errorCount: number;
  totalCost: number;
  avgLatency: number;
  successRate: number;
}

interface UseRequestLogsOptions {
  page: number;
  pageSize: number;
  filters?: RequestLogFormFilters;
  autoRefresh?: boolean;
  refreshInterval?: number;
}

interface UseRequestLogsResult {
  logs: RequestLogEntry[];
  totalCount: number;
  totalPages: number;
  currentPage: number;
  isLoading: boolean;
  error: Error | null;
  stats: RequestLogStats | null;
  refetch: () => Promise<void>;
}

export function requestLogFiltersToApiParams(
  filters: RequestLogFormFilters | undefined,
  page: number,
  pageSize: number,
) {
  return {
    page,
    pageSize,
    startDate: filters?.startDate?.toISOString(),
    endDate: filters?.endDate?.toISOString(),
    model: filters?.model,
    virtualKeyId: filters?.virtualKeyId?.toString(),
    statusCode: filters?.status,
  };
}

function toRequestLogEntry(item: LogRequestDto): RequestLogEntry {
  return {
    id: item.id ?? 0,
    virtualKeyId: item.virtualKeyId ?? 0,
    modelName: item.modelName ?? '',
    providerId: item.providerId ?? null,
    providerType: item.providerType ?? null,
    modelProviderMappingId: item.modelProviderMappingId ?? null,
    promptCachingEligible: item.promptCachingEligible ?? false,
    promptCachingPolicyApplied: item.promptCachingPolicyApplied ?? false,
    cachedReadSavings: item.cachedReadSavings ?? 0,
    cacheWritePremium: item.cacheWritePremium ?? 0,
    routingAffinityUsed: item.routingAffinityUsed ?? false,
    routingDecisionReason: item.routingDecisionReason ?? null,
    routingFailoverCount: item.routingFailoverCount ?? 0,
    requestType: item.requestType ?? '',
    inputTokens: item.inputTokens ?? 0,
    outputTokens: item.outputTokens ?? 0,
    cachedInputTokens: item.cachedInputTokens ?? null,
    cachedWriteTokens: item.cachedWriteTokens ?? null,
    cost: item.cost ?? 0,
    billingMethod: item.billingMethod ?? null,
    providerReportedCostUsd: item.providerReportedCostUsd ?? null,
    providerCostMarkupMultiplier: item.providerCostMarkupMultiplier ?? null,
    billedAtUtc: item.billedAtUtc ?? null,
    responseTimeMs: item.responseTimeMs ?? 0,
    userId: item.userId ?? null,
    clientIp: item.clientIp ?? null,
    requestPath: item.requestPath ?? null,
    statusCode: item.statusCode ?? null,
    timestamp: item.timestamp ?? '',
    metadata: item.metadata ?? null,
  };
}

/**
 * Hook for fetching paginated request logs with filtering
 */
export function useRequestLogs({
  page,
  pageSize,
  filters,
  autoRefresh = false,
  refreshInterval = 30000,
}: UseRequestLogsOptions): UseRequestLogsResult {
  const query = useQuery({
    queryKey: [
      'request-logs',
      page,
      pageSize,
      filters?.startDate?.toISOString(),
      filters?.endDate?.toISOString(),
      filters?.model,
      filters?.virtualKeyId,
      filters?.status,
    ],
    queryFn: async () => {
      const result = await withAdminClient(async (client) => {
        return client.analytics.getRequestLogs(
          requestLogFiltersToApiParams(filters, page, pageSize),
        );
      });

      const mappedLogs = (result.data ?? []).map(toRequestLogEntry);
      const pagination = result.pagination;

      let stats: RequestLogStats;
      if (mappedLogs.length > 0) {
        const successCount = mappedLogs.filter(
          (log) => log.statusCode === null || (log.statusCode >= 200 && log.statusCode < 400)
        ).length;
        const errorCount = mappedLogs.filter(
          (log) => log.statusCode !== null && log.statusCode >= 400
        ).length;
        const totalCost = mappedLogs.reduce((sum, log) => sum + log.cost, 0);
        const avgLatency =
          mappedLogs.reduce((sum, log) => sum + log.responseTimeMs, 0) / mappedLogs.length;

        stats = {
          totalRequests: pagination?.totalItems ?? mappedLogs.length,
          successCount,
          errorCount,
          totalCost,
          avgLatency,
          successRate: (successCount / mappedLogs.length) * 100,
        };
      } else {
        stats = {
          totalRequests: pagination?.totalItems ?? 0,
          successCount: 0,
          errorCount: 0,
          totalCost: 0,
          avgLatency: 0,
          successRate: 0,
        };
      }

      return {
        logs: mappedLogs,
        totalCount: pagination?.totalItems ?? 0,
        totalPages: pagination?.totalPages ?? Math.ceil((pagination?.totalItems ?? 0) / pageSize),
        currentPage: pagination?.page ?? page,
        stats,
      };
    },
    refetchInterval: autoRefresh ? refreshInterval : false,
  });

  let queryError: Error | null = null;
  if (query.error) {
    queryError = query.error instanceof Error
      ? query.error
      : new Error('Failed to fetch request logs');
  }

  return {
    logs: query.data?.logs ?? [],
    totalCount: query.data?.totalCount ?? 0,
    totalPages: query.data?.totalPages ?? 0,
    currentPage: query.data?.currentPage ?? page,
    isLoading: query.isFetching,
    error: queryError,
    stats: query.data?.stats ?? null,
    refetch: async () => {
      await query.refetch();
    },
  };
}

/**
 * Hook for fetching distinct model names for filter dropdown
 */
export function useDistinctModels() {
  const query = useQuery({
    queryKey: ['request-log-models'],
    queryFn: async () => {
      const result = await withAdminClient(client =>
        client.analytics.getRequestLogs({ page: 1, pageSize: 100 })
      );
      return [
        ...new Set(
          (result.data ?? [])
            .map(item => item.modelName ?? '')
            .filter(model => model !== '')
        ),
      ].sort();
    },
  });

  return {
    models: query.data ?? [],
    isLoading: query.isFetching,
    error: query.error instanceof Error ? query.error : null,
  };
}
