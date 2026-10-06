import { ModelType, PricingModel } from '@/lib/admin-api';

import {
  createModelCostFormValues,
  modelCostToFormValues,
  toCreateModelCostDto,
  toUpdateModelCostDto,
} from './modelCostForm';

describe('modelCostForm', () => {
  it('preserves inactive state in create requests', () => {
    const values = createModelCostFormValues();
    values.costName = 'Disabled pricing';
    values.modelProviderTypeAssociationIds = [12];
    values.isActive = false;

    expect(toCreateModelCostDto(values)).toMatchObject({
      costName: 'Disabled pricing',
      modelProviderTypeAssociationIds: [12],
      isActive: false,
    });
  });

  it('uses the same mapping for edit requests', () => {
    const values = createModelCostFormValues();
    values.costName = 'Updated pricing';
    values.isActive = false;
    values.supportsBatchProcessing = true;
    values.batchProcessingMultiplier = 0.4;

    expect(toUpdateModelCostDto(values)).toMatchObject({
      costName: 'Updated pricing',
      isActive: false,
      supportsBatchProcessing: true,
      batchProcessingMultiplier: 0.4,
    });
  });

  it('hydrates the shared form from an existing model cost', () => {
    const values = modelCostToFormValues({
      id: 1,
      costName: 'Existing pricing',
      pricingModel: PricingModel.Standard,
      associatedModelAliases: ['model-a'],
      modelProviderTypeAssociationIds: [12],
      inputCostPerMillionTokens: 1,
      outputCostPerMillionTokens: 2,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      modelType: ModelType.Chat,
      isActive: false,
      effectiveDate: '2026-01-01T00:00:00Z',
      priority: 3,
      supportsBatchProcessing: false,
    });

    expect(values).toMatchObject({
      costName: 'Existing pricing',
      modelProviderTypeAssociationIds: [12],
      isActive: false,
      priority: 3,
    });
  });
});
