'use client';

import {
  Modal,
  TextInput,
  Switch,
  Stack,
  Group,
  NumberInput,
  Button,
  Select,
  JsonInput,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { useEffect } from 'react';
import { useUpdateModelMapping, useModelMappings } from '@/hooks/useModelMappingsApi';
import { useProviders } from '@/hooks/useProviderApi';
import { ProviderType, type ProviderDto, type ModelProviderMappingDto, type UpdateModelProviderMappingDto } from '@/lib/admin-api';
import { getProviderTypeFromDto, getProviderDisplayName } from '@/lib/utils/providerTypeUtils';
import { createMappingFormValidation } from './mappingFormValidation';

interface EditModelMappingModalProps {
  isOpen: boolean;
  onClose: () => void;
  mapping: ModelProviderMappingDto | null;
  onSave?: () => void;
}

interface FormValues {
  modelAlias: string;
  providerId: string;
  providerModelId: string;
  modelProviderTypeAssociationId?: number;
  priority: number;
  isEnabled: boolean;
  providerOptions?: string;
}

export function EditModelMappingModal({
  isOpen,
  onClose,
  mapping,
  onSave,
}: EditModelMappingModalProps) {
  const updateMapping = useUpdateModelMapping();
  const { providers } = useProviders();
  const { mappings } = useModelMappings();

  const form = useForm<FormValues>({
    initialValues: {
      modelAlias: '',
      providerId: '',
      providerModelId: '',
      modelProviderTypeAssociationId: undefined,
      priority: 100,
      isEnabled: true,
      providerOptions: undefined,
    },
    validate: createMappingFormValidation(mappings, mapping?.id),
  });

  // Update form when mapping changes
  useEffect(() => {
    if (mapping) {
      form.setValues({
        modelAlias: mapping.modelAlias,
        providerId: mapping.providerId?.toString() ?? '',
        providerModelId: mapping.providerModelId,
        modelProviderTypeAssociationId: mapping.modelProviderTypeAssociationId,
        priority: mapping.priority ?? 100,
        isEnabled: mapping.isEnabled,
        providerOptions: mapping.providerOptions
          ? JSON.stringify(mapping.providerOptions, null, 2)
          : undefined,
      });
      form.resetDirty();
    }
    // The form object is intentionally excluded: Mantine returns a new wrapper while its
    // setters are stable, and depending on the wrapper causes an initialization render loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapping]);

  const handleClose = () => {
    form.reset();
    onClose();
  };

  const handleSubmit = async (values: FormValues) => {
    if (!mapping) return;

    const updateData: UpdateModelProviderMappingDto = {
      modelAlias: values.modelAlias,
      providerId: parseInt(values.providerId, 10), // Send numeric ID directly
      providerModelId: values.providerModelId,
      modelProviderTypeAssociationId: values.modelProviderTypeAssociationId ?? mapping.modelProviderTypeAssociationId,
      priority: values.priority,
      weight: mapping.weight,
      isEnabled: values.isEnabled,
      providerOptions: values.providerOptions?.trim()
        ? JSON.parse(values.providerOptions) as Record<string, unknown>
        : {},
    };

    try {
      await updateMapping.mutateAsync({
        id: mapping.id,
        data: updateData,
      });

      onSave?.();
      handleClose();
    } catch (error) {
      console.error('[EditModal] Update failed:', error);
    }
  };

  const providerOptions = providers?.map((p: ProviderDto) => {
    try {
      const providerType = getProviderTypeFromDto(p);
      return {
        value: p.id?.toString() ?? '', // Form uses string representation of numeric ID
        label: getProviderDisplayName(providerType),
      };
    } catch {
      return {
        value: p.id?.toString() ?? '',
        label: 'Unknown Provider',
      };
    }
  }).filter(opt => opt.value !== '') || [];

  const selectedProvider = providers?.find((p: ProviderDto) => p.id?.toString() === form.values.providerId);
  let isOpenRouter = false;
  if (selectedProvider) {
    try {
      isOpenRouter = getProviderTypeFromDto(selectedProvider) === ProviderType.OpenRouter;
    } catch {
      isOpenRouter = false;
    }
  }

  return (
    <Modal
      opened={isOpen}
      onClose={handleClose}
      title="Edit Model Mapping"
      size="lg"
    >
      <form onSubmit={form.onSubmit(handleSubmit)}>
        <Stack gap="md">
          <TextInput
            label="Model Alias"
            placeholder="e.g., gpt-4-turbo"
            description="The alias used to reference this model in API calls"
            required
            {...form.getInputProps('modelAlias')}
          />


          <Select
            label="Provider"
            placeholder="Select provider"
            data={providerOptions}
            {...form.getInputProps('providerId')}
          />

          <TextInput
            label="Provider Model ID"
            placeholder="e.g., gpt-4-turbo"
            description="The model identifier used by the provider"
            {...form.getInputProps('providerModelId')}
          />

          <NumberInput
            label="Priority"
            placeholder="100"
            description="Higher priority mappings are preferred (0-1000)"
            min={0}
            max={1000}
            {...form.getInputProps('priority')}
          />

          <Switch
            label="Enable mapping"
            description="Disabled mappings will not be used for routing"
            {...form.getInputProps('isEnabled', { type: 'checkbox' })}
          />


          {isOpenRouter && (
            <JsonInput
              label="Provider request options (JSON)"
              placeholder='{"provider": {"order": ["anthropic"]}, "plugins": [...]}'
              description="OpenRouter routing options merged into every request for this mapping (provider, plugins, transforms, models, route). Must be a JSON object; model/messages/stream are not allowed."
              validationError="Invalid JSON"
              formatOnBlur
              autosize
              minRows={3}
              {...form.getInputProps('providerOptions')}
            />
          )}

          <Group justify="flex-end" mt="md">
            <Button variant="subtle" onClick={handleClose}>
              Cancel
            </Button>
            <Button 
              type="submit" 
              loading={updateMapping.isPending}
            >
              Save Changes
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
