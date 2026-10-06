'use client';

import { useMutation } from '@tanstack/react-query';
import { withAdminClient } from '@/lib/client/adminClient';
import { useAdminMutation } from '@/hooks/useAdminMutation';
import { notify } from '@/lib/notifications';
import { downloadFile, serializeModelCostsToCsv } from '../utils/csvHelpers';
import type {
  ModelCost,
  CreateModelCostDto,
  UpdateModelCostDto,
  ModelCostListResponse,
  ModelCostFilters,
} from '../types/modelCost';
import { ModelType } from '@/lib/admin-api';

/**
 * Fetch model costs with pagination and filters.
 * This is a standalone async function (not a hook) for use inside useQuery.
 */
export async function fetchModelCosts(page = 1, pageSize = 50, filters?: ModelCostFilters): Promise<ModelCostListResponse> {
  const result = await withAdminClient(client =>
    client.modelCosts.list({
      page,
      pageSize,
      provider: filters?.providerId,
      isActive: filters?.isActive,
      modelType: filters?.modelType,
    })
  );

  return {
    items: result.items,
    totalCount: result.totalCount,
    page: result.page,
    pageSize: result.pageSize,
    totalPages: result.totalPages ?? Math.ceil(result.totalCount / result.pageSize),
  } as ModelCostListResponse;
}

export async function fetchModelCostById(id: number): Promise<ModelCost> {
  return withAdminClient(client => client.modelCosts.getById(id));
}

/**
 * Find a model cost by pattern matching on name or aliases.
 */
// --- Mutation hooks using useAdminMutation ---

export function useCreateModelCost() {
  return useAdminMutation<ModelCost, CreateModelCostDto>({
    mutationFn: (data) => (client) => client.modelCosts.create(data),
    successMessage: 'Model pricing created successfully',
    invalidateKeys: ['model-costs'],
  });
}

export function useUpdateModelCost() {
  return useAdminMutation<ModelCost, { id: number; data: UpdateModelCostDto }>({
    mutationFn: ({ id, data }) => (client) => client.modelCosts.update(id, data),
    successMessage: 'Model pricing updated successfully',
    invalidateKeys: ['model-costs'],
  });
}

export function useDeleteModelCost() {
  return useAdminMutation<void, number>({
    mutationFn: (id) => (client) => client.modelCosts.deleteById(id),
    successMessage: 'Model pricing deleted successfully',
    invalidateKeys: ['model-costs'],
  });
}

export interface ModelCostImportInput {
  costName: string;
  modelAliases: string[];
  modelType: string;
  inputCostPerMillionTokens: number;
  outputCostPerMillionTokens: number;
  cachedInputCostPerMillionTokens?: number;
  cachedInputWriteCostPerMillionTokens?: number;
  embeddingCostPerMillionTokens?: number;
  costPerSearchUnit?: number;
  supportsBatchProcessing?: boolean;
  batchProcessingMultiplier?: number;
  priority?: number;
  description?: string;
  isActive?: boolean;
}

export interface ModelCostImportSummary {
  success: number;
  failed: number;
  errors: Array<{ costName: string; error: string }>;
}

export function useImportModelCostsWithAliases() {
  return useMutation<ModelCostImportSummary, Error, ModelCostImportInput[]>({
    mutationFn: async items => withAdminClient(async client => {
      const mappings = await client.modelMappings.list();
      const associationIdByAlias = new Map<string, number>();
      for (const mapping of mappings) {
        const associationId = mapping.modelProviderTypeAssociationId;
        if (!associationId) continue;
        associationIdByAlias.set(mapping.modelAlias.toLowerCase(), associationId);
        associationIdByAlias.set(mapping.providerModelId.toLowerCase(), associationId);
      }

      const preflightErrors: ModelCostImportSummary['errors'] = [];
      const importable: CreateModelCostDto[] = [];
      for (const item of items) {
        const associationIds = [...new Set(
          item.modelAliases
            .map(alias => associationIdByAlias.get(alias.toLowerCase()))
            .filter((id): id is number => id !== undefined),
        )];
        const unresolvedAliases = item.modelAliases.filter(
          alias => !associationIdByAlias.has(alias.toLowerCase()),
        );
        if (associationIds.length === 0 || unresolvedAliases.length > 0) {
          preflightErrors.push({
            costName: item.costName,
            error: unresolvedAliases.length > 0
              ? `No model mapping found for: ${unresolvedAliases.join(', ')}`
              : 'No model mappings were resolved',
          });
          continue;
        }

        importable.push({
          costName: item.costName,
          modelProviderTypeAssociationIds: associationIds,
          modelType: item.modelType as ModelType,
          inputCostPerMillionTokens: item.inputCostPerMillionTokens,
          outputCostPerMillionTokens: item.outputCostPerMillionTokens,
          cachedInputCostPerMillionTokens: item.cachedInputCostPerMillionTokens,
          cachedInputWriteCostPerMillionTokens: item.cachedInputWriteCostPerMillionTokens,
          embeddingCostPerMillionTokens: item.embeddingCostPerMillionTokens,
          costPerSearchUnit: item.costPerSearchUnit,
          supportsBatchProcessing: item.supportsBatchProcessing,
          batchProcessingMultiplier: item.batchProcessingMultiplier,
          priority: item.priority,
          description: item.description,
          isActive: item.isActive,
        });
      }

      if (importable.length === 0) {
        return { success: 0, failed: preflightErrors.length, errors: preflightErrors };
      }

      const result = await client.modelCosts.import(importable);
      const apiErrors = (result.errors ?? []).map(error => ({
        costName: importable[error.row - 1]?.costName ?? 'Unknown',
        error: error.error,
      }));
      return {
        success: result.success ?? 0,
        failed: (result.failed ?? 0) + preflightErrors.length,
        errors: [...preflightErrors, ...apiErrors],
      };
    }),
    onSuccess: result => {
      if (result.success > 0) notify.success(`Successfully imported ${result.success} model costs`);
      if (result.failed > 0) {
        notify.warning(result.errors.map(error => `${error.costName}: ${error.error}`).join('\n'));
      }
    },
    onError: error => notify.error(error, 'Failed to import model costs'),
  });
}

async function fetchAllModelCosts(): Promise<ModelCost[]> {
  const pageSize = 250;
  const firstPage = await fetchModelCosts(1, pageSize);
  const costs = [...firstPage.items];
  for (let page = 2; page <= firstPage.totalPages; page += 1) {
    costs.push(...(await fetchModelCosts(page, pageSize)).items);
  }
  return costs;
}

export function useExportModelCosts() {
  return useMutation<void, Error, 'csv' | 'json'>({
    mutationFn: async format => {
      const costs = await fetchAllModelCosts();
      const contents = format === 'csv'
        ? serializeModelCostsToCsv(costs)
        : JSON.stringify(costs, null, 2);
      const contentType = format === 'csv' ? 'text/csv;charset=utf-8' : 'application/json';
      const date = new Date().toISOString().slice(0, 10);
      downloadFile(new Blob([contents], { type: contentType }), `model-costs-${date}.${format}`);
    },
    onSuccess: () => notify.success('Model costs exported successfully'),
    onError: error => notify.error(error, 'Failed to export model costs'),
  });
}
