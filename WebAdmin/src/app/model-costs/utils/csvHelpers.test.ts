import { parseCSVContent, parseCSVLine, serializeModelCostsToCsv } from './csvHelpers';
import { ModelType, PricingModel } from '@/lib/admin-api';

describe('model cost CSV helpers', () => {
  it('parses quoted aliases and supported pricing fields', () => {
    const csv = `Cost Name,Associated Model Aliases,Model Type,Input Cost (per million tokens),Output Cost (per million tokens),Cached Input Cost (per million tokens),Search Unit Cost (per 1K units),Active\nExample,"model-a,model-b",chat,1.5,2.5,0.5,0.25,true`;
    const [cost] = parseCSVContent(csv);

    expect(cost).toMatchObject({
      costName: 'Example',
      modelAliases: ['model-a', 'model-b'],
      cachedInputCostPerMillion: 0.5,
      searchUnitCostPer1K: 0.25,
      active: true,
      isValid: true,
    });
  });

  it('round-trips the exported columns through the importer', () => {
    const csv = serializeModelCostsToCsv([{
      id: 1,
      costName: 'Pricing, "quoted"',
      pricingModel: PricingModel.Standard,
      associatedModelAliases: ['model-a'],
      modelProviderTypeAssociationIds: [10],
      inputCostPerMillionTokens: 1,
      outputCostPerMillionTokens: 2,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      effectiveDate: '2026-01-01T00:00:00Z',
      modelType: ModelType.Chat,
      isActive: true,
      priority: 0,
      supportsBatchProcessing: false,
    }]);

    expect(parseCSVContent(csv)[0]).toMatchObject({
      costName: 'Pricing, "quoted"',
      modelAliases: ['model-a'],
      inputCostPerMillion: 1,
      outputCostPerMillion: 2,
      isValid: true,
    });
  });

  it('handles escaped quotes', () => {
    expect(parseCSVLine('model,"value with ""quotes"""')).toEqual(['model', 'value with "quotes"']);
  });
});
