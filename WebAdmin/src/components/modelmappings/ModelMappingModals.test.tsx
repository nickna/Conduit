import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import { useModelAssociations } from '@/hooks/useModelAssociations';
import {
  useCreateModelMapping,
  useModelMappings,
  useUpdateModelMapping,
} from '@/hooks/useModelMappingsApi';
import { useModels } from '@/hooks/useModelsApi';
import { useProviders } from '@/hooks/useProviderApi';
import type { ModelProviderMappingDto } from '@/lib/admin-api';
import { CreateModelMappingModal } from './CreateModelMappingModal';
import { EditModelMappingModal } from './EditModelMappingModal';

jest.mock('@/hooks/useModelAssociations', () => ({ useModelAssociations: jest.fn() }));
jest.mock('@/hooks/useModelMappingsApi', () => ({
  useCreateModelMapping: jest.fn(),
  useModelMappings: jest.fn(),
  useUpdateModelMapping: jest.fn(),
}));
jest.mock('@/hooks/useModelsApi', () => ({ useModels: jest.fn() }));
jest.mock('@/hooks/useProviderApi', () => ({ useProviders: jest.fn() }));
jest.mock('@/lib/notifications', () => ({
  notify: { error: jest.fn(), success: jest.fn(), warning: jest.fn() },
}));
jest.mock('@/lib/utils/providerTypeUtils', () => ({
  getProviderTypeFromDto: jest.fn(() => 'openRouter'),
  getProviderDisplayName: jest.fn(() => 'OpenRouter'),
}));
jest.mock('./AssociationProviderSelect', () => ({
  AssociationProviderSelect: ({
    onChange,
  }: {
    onChange: (value: string) => void;
  }) => (
    <button type="button" onClick={() => onChange('42:9')}>
      Choose provider configuration
    </button>
  ),
}));

const createMapping = jest.fn();
const updateMapping = jest.fn();

beforeEach(() => {
  jest.clearAllMocks();
  createMapping.mockResolvedValue({ id: 7 });
  updateMapping.mockResolvedValue(undefined);
  jest.mocked(useCreateModelMapping).mockReturnValue({
    mutateAsync: createMapping,
    isPending: false,
  } as unknown as ReturnType<typeof useCreateModelMapping>);
  jest.mocked(useUpdateModelMapping).mockReturnValue({
    mutateAsync: updateMapping,
    isPending: false,
  } as unknown as ReturnType<typeof useUpdateModelMapping>);
  jest.mocked(useModels).mockReturnValue({
    models: [{ id: 1, name: 'shared-alias', supportsChat: true }],
    isLoading: false,
  } as ReturnType<typeof useModels>);
  jest.mocked(useModelAssociations).mockReturnValue({
    data: [{
      associationId: 42,
      identifier: 'provider/shared',
      provider: 13,
      providerVariation: null,
      maxInputTokens: null,
      maxOutputTokens: null,
      speedScore: null,
      qualityScore: null,
      isPrimary: true,
      availableProviders: [{ providerId: 9, providerName: 'OpenRouter', providerType: 'openRouter' }],
    }],
    isLoading: false,
  } as unknown as ReturnType<typeof useModelAssociations>);
  jest.mocked(useProviders).mockReturnValue({
    providers: [{
      id: 9,
      providerType: 'openRouter',
      providerName: 'OpenRouter',
      keyCount: 1,
      trustProviderReportedCosts: false,
      providerCostMarkupMultiplier: 1,
      isEnabled: true,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    }],
    isLoading: false,
  } as unknown as ReturnType<typeof useProviders>);
});

it('creates a same-alias mapping for a different provider with exact foreign keys', async () => {
  jest.mocked(useModelMappings).mockReturnValue({
    mappings: [mapping({ id: 8, providerId: 10 })],
    isLoading: false,
  } as ReturnType<typeof useModelMappings>);

  render(
    <MantineProvider>
      <CreateModelMappingModal isOpen onClose={jest.fn()} />
    </MantineProvider>,
  );

  fireEvent.click(screen.getByPlaceholderText('Select a model'));
  fireEvent.click(await screen.findByText('shared-alias'));
  fireEvent.click(await screen.findByRole('button', { name: 'Choose provider configuration' }));
  fireEvent.click(screen.getByRole('button', { name: 'Create Mapping' }));

  await waitFor(() => expect(createMapping).toHaveBeenCalledWith({
    modelAlias: 'shared-alias',
    providerId: 9,
    providerModelId: 'provider/shared',
    modelProviderTypeAssociationId: 42,
    priority: 100,
    weight: 1,
    isEnabled: true,
  }));
});

it('updates with the existing association and weight and explicitly clears provider options', async () => {
  jest.mocked(useModelMappings).mockReturnValue({
    mappings: [mapping({ id: 8, providerId: 10 })],
    isLoading: false,
  } as ReturnType<typeof useModelMappings>);
  const editedMapping = mapping({
    providerOptions: { route: 'old' },
    weight: 1.4,
  });

  render(
    <MantineProvider>
      <EditModelMappingModal
        isOpen
        onClose={jest.fn()}
        mapping={editedMapping}
      />
    </MantineProvider>,
  );

  const options = await screen.findByLabelText('Provider request options (JSON)');
  fireEvent.change(options, { target: { value: '' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }));

  await waitFor(() => expect(updateMapping).toHaveBeenCalledWith({
    id: 7,
    data: {
      modelAlias: 'shared-alias',
      providerId: 9,
      providerModelId: 'provider/shared',
      modelProviderTypeAssociationId: 42,
      priority: 100,
      weight: 1.4,
      isEnabled: true,
      providerOptions: {},
    },
  }));
});

function mapping(overrides: Partial<ModelProviderMappingDto> = {}): ModelProviderMappingDto {
  return { ...mappingBase(), ...overrides };
}

function mappingBase(): ModelProviderMappingDto {
  return {
    id: 7,
    modelAlias: 'shared-alias',
    providerModelId: 'provider/shared',
    providerId: 9,
    modelProviderTypeAssociationId: 42,
    priority: 100,
    weight: 1,
    isEnabled: true,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    provider: null,
    providerOptions: undefined,
    capabilities: null,
  };
}
