// Re-export types from SDK
export type {
  ModelCostDto as ModelCost,
  CreateModelCostDto,
  UpdateModelCostDto,
  ModelCostFilters,
} from '@/lib/admin-api';

import type { ModelCostDto } from '@/lib/admin-api';

// Helper type for list responses
export interface ModelCostListResponse {
  items: ModelCostDto[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}
