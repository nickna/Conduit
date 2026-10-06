import { FilterOptions } from './common';
import { ProviderType } from './providerType';
import { ModelType } from './modelType';

/** Pricing model type that determines how costs are calculated */
export const PricingModel = {
  Standard: 'standard',
  PerVideo: 'perVideo',
  PerSecondVideo: 'perSecondVideo',
  InferenceSteps: 'inferenceSteps',
  TieredTokens: 'tieredTokens',
  PerImage: 'perImage',
  RulesBased: 'rulesBased',
} as const;
export type PricingModel = (typeof PricingModel)[keyof typeof PricingModel];

// Matches the wire `ModelCostDto` (modelType narrows the wire string to a client enum — asserted
// with SameKeys). Media/inference pricing is carried in the polymorphic pricingConfiguration JSON,
// not as flat fields; the legacy image/video/inference/extra-audio flat fields were removed and
// audioCostPerKCharacters was corrected to audioCostPerThousandCharacters in issue #1038.
export interface ModelCostDto {
  id: number;
  costName: string; // User-friendly name like "GPT-4 Standard Pricing"
  pricingModel: PricingModel; // The pricing model type
  pricingConfiguration?: string; // JSON configuration for polymorphic pricing
  associatedModelAliases: string[]; // Model aliases using this cost
  modelProviderTypeAssociationIds: number[];
  inputCostPerMillionTokens: number; // Cost per million tokens in USD
  outputCostPerMillionTokens: number; // Cost per million tokens in USD
  reasoningCostPerMillionTokens?: number; // Optional override; defaults to output token cost
  embeddingCostPerMillionTokens?: number; // Cost per million tokens in USD
  createdAt: string;
  updatedAt: string;
  modelType: ModelType;
  isActive: boolean;
  effectiveDate: string;
  expiryDate?: string;
  description?: string;
  priority: number;
  batchProcessingMultiplier?: number;
  supportsBatchProcessing: boolean;
  cachedInputCostPerMillionTokens?: number; // Cost per million tokens in USD
  cachedInputWriteCostPerMillionTokens?: number; // Cost per million tokens in USD
  costPerSearchUnit?: number;
  audioCostPerMinute?: number;
  audioCostPerThousandCharacters?: number;
}

// Matches the wire `CreateModelCostDto` (SameKeys — modelType narrows wire string). Media/inference
// pricing is set via pricingConfiguration; the ID field is modelProviderTypeAssociationIds. See #1038.
export interface CreateModelCostDto {
  costName: string; // Required: User-friendly name
  pricingModel?: PricingModel; // Default: Standard
  pricingConfiguration?: string; // JSON configuration for polymorphic pricing
  modelProviderTypeAssociationIds?: number[]; // IDs of provider/model type associations
  modelType?: ModelType; // Default: ModelType.Chat
  priority?: number; // Default: 0
  description?: string;
  isActive?: boolean; // Default: true
  inputCostPerMillionTokens: number; // Cost per million tokens in USD
  outputCostPerMillionTokens: number; // Cost per million tokens in USD
  reasoningCostPerMillionTokens?: number; // Optional override; defaults to output token cost
  embeddingCostPerMillionTokens?: number; // Cost per million tokens in USD
  batchProcessingMultiplier?: number;
  supportsBatchProcessing?: boolean;
  cachedInputCostPerMillionTokens?: number; // Cost per million tokens in USD
  cachedInputWriteCostPerMillionTokens?: number; // Cost per million tokens in USD
  costPerSearchUnit?: number;
  audioCostPerMinute?: number;
  audioCostPerThousandCharacters?: number;
}

// Matches the wire `UpdateModelCostDto` (SameKeys — modelType narrows wire string). See issue #1038.
export interface UpdateModelCostDto {
  costName: string; // Required: User-friendly name
  pricingModel?: PricingModel;
  pricingConfiguration?: string; // JSON configuration for polymorphic pricing
  modelProviderTypeAssociationIds?: number[]; // IDs of provider/model type associations
  modelType?: ModelType;
  priority?: number;
  description?: string;
  isActive?: boolean;
  inputCostPerMillionTokens?: number; // Cost per million tokens in USD
  outputCostPerMillionTokens?: number; // Cost per million tokens in USD
  reasoningCostPerMillionTokens?: number; // Optional override; defaults to output token cost
  embeddingCostPerMillionTokens?: number; // Cost per million tokens in USD
  batchProcessingMultiplier?: number;
  supportsBatchProcessing?: boolean;
  cachedInputCostPerMillionTokens?: number; // Cost per million tokens in USD
  cachedInputWriteCostPerMillionTokens?: number; // Cost per million tokens in USD
  costPerSearchUnit?: number;
  audioCostPerMinute?: number;
  audioCostPerThousandCharacters?: number;
}

export interface ModelCostFilters extends FilterOptions {
  modelId?: string;
  providerId?: string;
  currency?: string;
  isActive?: boolean;
  effectiveAfter?: string;
  effectiveBefore?: string;
  minInputCost?: number;
  maxInputCost?: number;
  minOutputCost?: number;
  maxOutputCost?: number;
  /** Filter by model type (chat, image, video, embedding, audio) */
  modelType?: string;
}

export interface ModelCostCalculation {
  modelId: string;
  inputTokens: number;
  outputTokens: number;
  inputCost: number;
  outputCost: number;
  totalCost: number;
  currency: string;
  costPerMillionInputTokens: number;
  costPerMillionOutputTokens: number;
}

export interface ModelCostHistory {
  modelId: string;
  history: {
    id: number;
    inputCostPerMillionTokens: number;
    outputCostPerMillionTokens: number;
    effectiveDate: string;
    expiryDate?: string;
    changeReason?: string;
  }[];
}

export interface CostEstimate {
  scenarios: {
    name: string;
    inputTokens: number;
    outputTokens: number;
  }[];
  models: string[];
  results: {
    scenario: string;
    costs: {
      modelId: string;
      totalCost: number;
      inputCost: number;
      outputCost: number;
      currency: string;
    }[];
  }[];
  recommendations?: {
    mostCostEffective: string;
    bestValueForMoney: string;
    notes: string[];
  };
}

export interface ModelCostComparison {
  baseModel: string;
  comparisonModels: string[];
  inputTokens: number;
  outputTokens: number;
  results: {
    modelId: string;
    totalCost: number;
    costDifference: number;
    percentageDifference: number;
    currency: string;
  }[];
}

export interface ModelCostOverview {
  modelName: string;
  providerType: ProviderType;
  modelType: ModelType;
  totalRequests: number;
  totalTokens: number;
  totalCost: number;
  averageCostPerRequest: number;
  costTrend: 'increasing' | 'decreasing' | 'stable';
  trendPercentage: number;
}

export interface CostTrend {
  date: string;
  cost: number;
  requests: number;
  tokens: number;
}

export interface ModelCostImportResult {
  success: number;
  failed: number;
  errors: Array<{ row: number; error: string }>;
}
