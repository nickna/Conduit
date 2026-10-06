import {
  ModelType,
  PricingModel,
  type CreateModelCostDto,
  type ModelCostDto,
  type UpdateModelCostDto,
} from '@/lib/admin-api';

export interface ModelCostFormValues {
  costName: string;
  modelProviderTypeAssociationIds: number[];
  pricingModel: PricingModel;
  pricingConfiguration: string;
  modelType: ModelType;
  inputCostPerMillion: number;
  outputCostPerMillion: number;
  cachedInputCostPerMillion: number;
  cachedInputWriteCostPerMillion: number;
  embeddingCostPerMillion: number;
  audioCostPerMinute: number;
  audioCostPerKCharacters: number;
  supportsBatchProcessing: boolean;
  batchProcessingMultiplier: number;
  priority: number;
  description: string;
  isActive: boolean;
}

export function createModelCostFormValues(): ModelCostFormValues {
  return {
    costName: '',
    modelProviderTypeAssociationIds: [],
    pricingModel: PricingModel.Standard,
    pricingConfiguration: '',
    modelType: ModelType.Chat,
    inputCostPerMillion: 0,
    outputCostPerMillion: 0,
    cachedInputCostPerMillion: 0,
    cachedInputWriteCostPerMillion: 0,
    embeddingCostPerMillion: 0,
    audioCostPerMinute: 0,
    audioCostPerKCharacters: 0,
    supportsBatchProcessing: false,
    batchProcessingMultiplier: 0.5,
    priority: 0,
    description: '',
    isActive: true,
  };
}

export function modelCostToFormValues(modelCost: ModelCostDto): ModelCostFormValues {
  return {
    ...createModelCostFormValues(),
    costName: modelCost.costName,
    modelProviderTypeAssociationIds: modelCost.modelProviderTypeAssociationIds,
    pricingModel: modelCost.pricingModel ?? PricingModel.Standard,
    pricingConfiguration: modelCost.pricingConfiguration ?? '',
    modelType: modelCost.modelType,
    inputCostPerMillion: modelCost.inputCostPerMillionTokens ?? 0,
    outputCostPerMillion: modelCost.outputCostPerMillionTokens ?? 0,
    cachedInputCostPerMillion: modelCost.cachedInputCostPerMillionTokens ?? 0,
    cachedInputWriteCostPerMillion: modelCost.cachedInputWriteCostPerMillionTokens ?? 0,
    embeddingCostPerMillion: modelCost.embeddingCostPerMillionTokens ?? 0,
    audioCostPerMinute: modelCost.audioCostPerMinute ?? 0,
    audioCostPerKCharacters: modelCost.audioCostPerThousandCharacters ?? 0,
    supportsBatchProcessing: modelCost.supportsBatchProcessing ?? false,
    batchProcessingMultiplier: modelCost.batchProcessingMultiplier ?? 0.5,
    priority: modelCost.priority,
    description: modelCost.description ?? '',
    isActive: modelCost.isActive,
  };
}

export const modelCostFormValidation = {
  costName: (value: string) => (!value.trim() ? 'Cost name is required' : null),
  modelProviderTypeAssociationIds: (value: number[]) =>
    value.length === 0 ? 'At least one model must be selected' : null,
  priority: (value: number) => (value < 0 ? 'Priority must be non-negative' : null),
  pricingConfiguration: (value: string, values: ModelCostFormValues) => {
    if (values.pricingModel !== PricingModel.Standard && !value) {
      return 'Configuration is required for this pricing model';
    }
    if (value) {
      try {
        JSON.parse(value);
      } catch {
        return 'Invalid JSON format';
      }
    }
    return null;
  },
};

function toRequestFields(values: ModelCostFormValues) {
  return {
    costName: values.costName,
    modelProviderTypeAssociationIds: values.modelProviderTypeAssociationIds,
    pricingModel: values.pricingModel,
    pricingConfiguration: values.pricingConfiguration || undefined,
    modelType: values.modelType,
    priority: values.priority,
    description: values.description || undefined,
    isActive: values.isActive,
    inputCostPerMillionTokens: values.inputCostPerMillion,
    outputCostPerMillionTokens: values.outputCostPerMillion,
    cachedInputCostPerMillionTokens: values.cachedInputCostPerMillion || undefined,
    cachedInputWriteCostPerMillionTokens: values.cachedInputWriteCostPerMillion || undefined,
    embeddingCostPerMillionTokens: values.embeddingCostPerMillion || undefined,
    audioCostPerMinute: values.audioCostPerMinute || undefined,
    audioCostPerThousandCharacters: values.audioCostPerKCharacters || undefined,
    supportsBatchProcessing: values.supportsBatchProcessing,
    batchProcessingMultiplier: values.supportsBatchProcessing
      ? values.batchProcessingMultiplier
      : undefined,
  };
}

export function toCreateModelCostDto(values: ModelCostFormValues): CreateModelCostDto {
  return toRequestFields(values);
}

export function toUpdateModelCostDto(values: ModelCostFormValues): UpdateModelCostDto {
  return toRequestFields(values);
}
