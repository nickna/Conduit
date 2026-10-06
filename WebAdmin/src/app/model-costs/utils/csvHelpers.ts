import type { ModelCost } from '../types/modelCost';
import { downloadBlob } from '@/lib/utils/export';

export interface ParsedModelCost {
  costName: string;
  modelAliases: string[];
  modelType: string;
  inputCostPerMillion: number;
  outputCostPerMillion: number;
  cachedInputCostPerMillion?: number;
  cachedInputWriteCostPerMillion?: number;
  embeddingCostPerMillion?: number;
  batchProcessingMultiplier?: number;
  supportsBatchProcessing: boolean;
  searchUnitCostPer1K?: number;
  priority: number;
  active: boolean;
  description?: string;
  // Validation
  isValid: boolean;
  errors: string[];
  // Tracking
  rowNumber: number;
  isSkipped?: boolean;
  skipReason?: string;
}

export const parseCSVLine = (line: string): string[] => {
  const result: string[] = [];
  let current = '';
  let inQuotes = false;
  
  for (let i = 0; i < line.length; i++) {
    const char = line[i];
    const nextChar = line[i + 1];
    
    if (char === '"' && nextChar === '"' && inQuotes) {
      current += '"';
      i++; // Skip next quote
    } else if (char === '"') {
      inQuotes = !inQuotes;
    } else if (char === ',' && !inQuotes) {
      result.push(current.trim());
      current = '';
    } else {
      current += char;
    }
  }
  
  result.push(current.trim());
  return result;
};

export const parseCSVContent = (text: string): ParsedModelCost[] => {
  const lines = text.split('\n').filter(line => line.trim());
  
  if (lines.length < 2) {
    throw new Error('CSV file must contain headers and at least one data row');
  }

  // Parse headers
  const headers = parseCSVLine(lines[0]).map(h => h.trim().toLowerCase());
  const requiredHeaders = ['cost name', 'associated model aliases', 'model type'];
  
  for (const required of requiredHeaders) {
    if (!headers.some(h => h.includes(required))) {
      throw new Error(`Missing required column: ${required}`);
    }
  }

  // Parse data rows
  const parsed: ParsedModelCost[] = [];
  
  for (let i = 1; i < lines.length; i++) {
    const values = parseCSVLine(lines[i]);
    const rowNumber = i + 1;
    
    // Handle malformed rows
    if (values.length !== headers.length) {
      parsed.push({
        costName: '',
        modelAliases: [],
        modelType: '',
        inputCostPerMillion: 0,
        outputCostPerMillion: 0,
        priority: 0,
        active: false,
        supportsBatchProcessing: false,
        isValid: false,
        errors: [`Row has ${values.length} columns but expected ${headers.length}`],
        rowNumber,
        isSkipped: true,
        skipReason: `Malformed row: expected ${headers.length} columns, got ${values.length}`,
      });
      continue;
    }

    const row: Record<string, string> = {};
    headers.forEach((header, index) => {
      row[header] = values[index];
    });

    // Helper function to parse numeric values with proper fallbacks
    const parseNumericValue = (value: string | undefined, defaultValue?: number): number | undefined => {
      if (!value || value.trim() === '') return defaultValue;
      const parsed = parseFloat(value);
      return isNaN(parsed) ? defaultValue : parsed;
    };

    const cost: ParsedModelCost = {
      costName: row['cost name']?.trim() ?? '',
      modelAliases: row['associated model aliases']?.split(',').map(a => a.trim()).filter(a => a) ?? [],
      modelType: row['model type']?.trim().toLowerCase() ?? 'chat',
      inputCostPerMillion: parseNumericValue(row['input cost (per million tokens)'], 0) ?? 0,
      outputCostPerMillion: parseNumericValue(row['output cost (per million tokens)'], 0) ?? 0,
      cachedInputCostPerMillion: parseNumericValue(row['cached input cost (per million tokens)']),
      cachedInputWriteCostPerMillion: parseNumericValue(row['cache write cost (per million tokens)']),
      embeddingCostPerMillion: parseNumericValue(row['embedding cost (per million tokens)']),
      batchProcessingMultiplier: parseNumericValue(row['batch processing multiplier']),
      supportsBatchProcessing: row['supports batch processing']?.toLowerCase() === 'yes' || row['supports batch processing']?.toLowerCase() === 'true',
      searchUnitCostPer1K: parseNumericValue(row['search unit cost (per 1k units)']),
      priority: parseNumericValue(row['priority'], 0) ?? 0,
      active: row['active']?.toLowerCase() === 'yes' || row['active']?.toLowerCase() === 'true',
      description: row['description']?.trim(),
      isValid: true,
      errors: [],
      rowNumber,
    };

    // Validate row
    const errors: string[] = [];
    if (!cost.costName) errors.push('Cost name is required');
    if (cost.modelAliases.length === 0) errors.push('At least one model alias is required');
    if (!['chat', 'embedding', 'image', 'audio', 'video'].includes(cost.modelType)) {
      errors.push(`Invalid model type: ${cost.modelType}. Must be one of: chat, embedding, image, audio, video`);
    }
    if (cost.priority < 0) errors.push('Priority must be non-negative');
    
    // Cost validation
    if (cost.inputCostPerMillion < 0) errors.push('Input cost cannot be negative');
    if (cost.outputCostPerMillion < 0) errors.push('Output cost cannot be negative');
    if (cost.cachedInputCostPerMillion !== undefined && cost.cachedInputCostPerMillion < 0) errors.push('Cached input cost cannot be negative');
    if (cost.cachedInputWriteCostPerMillion !== undefined && cost.cachedInputWriteCostPerMillion < 0) errors.push('Cache write cost cannot be negative');
    if (cost.embeddingCostPerMillion !== undefined && cost.embeddingCostPerMillion < 0) errors.push('Embedding cost cannot be negative');
    if (cost.batchProcessingMultiplier !== undefined && cost.batchProcessingMultiplier < 0) errors.push('Batch processing multiplier cannot be negative');
    if (cost.batchProcessingMultiplier !== undefined && cost.batchProcessingMultiplier > 1) errors.push('Batch processing multiplier cannot be greater than 1 (>100% cost)');
    if (cost.searchUnitCostPer1K !== undefined && cost.searchUnitCostPer1K < 0) errors.push('Search unit cost cannot be negative');
    
    // Reasonable upper bounds validation
    if (cost.inputCostPerMillion > 1000000) errors.push('Input cost seems unreasonably high (>$1,000,000 per million tokens)');
    if (cost.outputCostPerMillion > 1000000) errors.push('Output cost seems unreasonably high (>$1,000,000 per million tokens)');

    cost.isValid = errors.length === 0;
    cost.errors = errors;

    parsed.push(cost);
  }

  // Check for duplicates
  const seenCostNames = new Set<string>();
  parsed.forEach(cost => {
    if (cost.isValid && seenCostNames.has(cost.costName)) {
      cost.isValid = false;
      cost.errors.push(`Duplicate cost name: ${cost.costName}`);
    } else if (cost.isValid) {
      seenCostNames.add(cost.costName);
    }
  });

  return parsed;
};

export const downloadFile = (blob: Blob, filename: string) => {
  downloadBlob(blob, filename);
};

const MODEL_COST_EXPORT_HEADERS = [
  'Cost Name',
  'Associated Model Aliases',
  'Model Type',
  'Input Cost (per million tokens)',
  'Output Cost (per million tokens)',
  'Cached Input Cost (per million tokens)',
  'Cache Write Cost (per million tokens)',
  'Embedding Cost (per million tokens)',
  'Search Unit Cost (per 1K units)',
  'Supports Batch Processing',
  'Batch Processing Multiplier',
  'Priority',
  'Active',
  'Description',
] as const;

function escapeCsvValue(value: unknown): string {
  let text = '';
  if (typeof value === 'string') text = value;
  else if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') text = String(value);
  else if (value !== null && value !== undefined) text = JSON.stringify(value);
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

/** Serialize current model-cost data using the same columns accepted by the importer. */
export function serializeModelCostsToCsv(costs: ModelCost[]): string {
  const rows = costs.map(cost => [
    cost.costName,
    cost.associatedModelAliases.join(','),
    cost.modelType,
    cost.inputCostPerMillionTokens,
    cost.outputCostPerMillionTokens,
    cost.cachedInputCostPerMillionTokens,
    cost.cachedInputWriteCostPerMillionTokens,
    cost.embeddingCostPerMillionTokens,
    cost.costPerSearchUnit,
    cost.supportsBatchProcessing,
    cost.batchProcessingMultiplier,
    cost.priority,
    cost.isActive,
    cost.description,
  ]);

  return [MODEL_COST_EXPORT_HEADERS, ...rows]
    .map(row => row.map(escapeCsvValue).join(','))
    .join('\r\n');
}
