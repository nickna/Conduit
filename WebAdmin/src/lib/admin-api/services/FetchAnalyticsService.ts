import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import type { RequestConfig } from '../client/types';
import type {
  RequestLogParams,
} from '../models/analytics';
import type { components } from '@/generated/admin-api';

type RequestLogPage = components['schemas']['PagedResultOfLogRequestDto'];

// Cost-related types
export interface CostDashboardDto {
  timeFrame: string;
  startDate: string;
  endDate: string;
  last24HoursCost: number;
  last7DaysCost: number;
  last30DaysCost: number;
  totalCost: number;
  topModelsBySpend: DetailedCostDataDto[];
  topProvidersBySpend: DetailedCostDataDto[];
  topVirtualKeysBySpend: DetailedCostDataDto[];
}

export interface DetailedCostDataDto {
  name: string;
  cost: number;
  percentage: number;
  requestCount: number;
}

export interface CostTrendDto {
  period: string;
  startDate: string;
  endDate: string;
  data: CostTrendDataDto[];
}

export interface CostTrendDataDto {
  date: string;
  cost: number;
  requestCount: number;
}

/**
 * Type-safe Analytics service using the Admin contract transport.
 */
export class FetchAnalyticsService {
  constructor(private readonly client: FetchBaseApiClient) {}

  /**
   * Get paginated request logs
   */
  async getRequestLogs(params?: RequestLogParams, config?: RequestConfig): Promise<RequestLogPage> {
    const query = {
      page: params?.page,
      pageSize: params?.pageSize,
      startDate: params?.startDate,
      endDate: params?.endDate,
      model: params?.model,
      virtualKeyId: params?.virtualKeyId ? Number(params.virtualKeyId) : undefined,
      status: params?.statusCode,
    };

    return this.client.executeContractRead(
      '/v1/admin/analytics/logs',
      (contractClient, options) => contractClient.GET('/v1/admin/analytics/logs', {
        ...options,
        params: { query },
      }),
      config,
    );
  }

  /**
   * Get cost dashboard summary
   */
  async getCostSummary(
    timeframe: string = 'daily',
    startDate?: string,
    endDate?: string,
    config?: RequestConfig
  ): Promise<CostDashboardDto> {
    return this.client.executeContractRead(
      '/v1/admin/analytics/costs/summary',
      (contractClient, options) => contractClient.GET('/v1/admin/analytics/costs/summary', {
        ...options,
        params: { query: { timeframe, startDate, endDate } },
      }),
      config,
    ) as Promise<CostDashboardDto>;
  }

  /**
   * Get cost trends over time
   */
  async getCostTrends(
    period: string = 'daily',
    startDate?: string,
    endDate?: string,
    config?: RequestConfig
  ): Promise<CostTrendDto> {
    return this.client.executeContractRead(
      '/v1/admin/analytics/costs/trends',
      (contractClient, options) => contractClient.GET('/v1/admin/analytics/costs/trends', {
        ...options,
        params: { query: { period, startDate, endDate } },
      }),
      config,
    ) as Promise<CostTrendDto>;
  }

  /**
   * Export analytics data in specified format
   * Returns the data as a Uint8Array for binary compatibility
   */
  async exportAnalyticsAsync(
    format: string = 'csv',
    startDate?: string,
    endDate?: string,
    model?: string,
    virtualKeyId?: number,
    config?: RequestConfig
  ): Promise<Uint8Array> {
    const buffer = await this.client.executeContractRead<ArrayBuffer>(
      '/v1/admin/analytics/export',
      (contractClient, options) => contractClient.GET('/v1/admin/analytics/export', {
        ...options,
        headers: {
          Accept: format === 'csv' ? 'text/csv' : 'application/json',
          ...options.headers,
        },
        params: { query: { format, startDate, endDate, model, virtualKeyId } },
        parseAs: 'arrayBuffer',
      }),
      config,
    );
    return new Uint8Array(buffer);
  }
}
