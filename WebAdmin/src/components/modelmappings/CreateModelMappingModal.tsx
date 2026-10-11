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
  Text,
  Alert,
  Paper,
  Badge,
  Flex,
  Divider,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconRobot, IconEye, IconBrush, IconVideo, IconBrain, IconAlertCircle } from '@tabler/icons-react';
import { useCreateModelMapping, useModelMappings } from '@/hooks/useModelMappingsApi';
import { useModels } from '@/hooks/useModelsApi';
import { useModelAssociations } from '@/hooks/useModelAssociations';
import { AssociationProviderSelect } from './AssociationProviderSelect';
import { notify } from '@/lib/notifications';
import type { CreateModelProviderMappingDto } from '@/lib/admin-api';
import { createMappingFormValidation } from './mappingFormValidation';

interface CreateModelMappingModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

interface FormValues {
  modelAlias: string;
  modelId: number | null;
  associationProviderId: string | null; // Format: "associationId:providerId"
  priority: number;
  isEnabled: boolean;
}

export function CreateModelMappingModal({ 
  isOpen, 
  onClose, 
  onSuccess 
}: CreateModelMappingModalProps) {
  const createMapping = useCreateModelMapping();
  const { models, isLoading: modelsLoading } = useModels();
  const { mappings } = useModelMappings();

  const form = useForm<FormValues>({
    initialValues: {
      modelAlias: '',
      modelId: null,
      associationProviderId: null,
      priority: 100,
      isEnabled: true,
    },
    validate: {
      ...createMappingFormValidation(mappings),
      modelId: (value) => !value ? 'Model selection is required' : null,
      associationProviderId: (value) => !value ? 'Provider configuration is required' : null,
    },
  });

  const { data: associations, isLoading: associationsLoading } = useModelAssociations(form.values.modelId);

  const handleClose = () => {
    form.reset();
    onClose();
  };

  const handleSubmit = async (values: FormValues) => {
    // Validate all fields before submission
    const validationErrors = form.validate();
    if (validationErrors.hasErrors) {
      // Show notification about validation errors
      notify.error(new Error('Please fill in all required fields correctly'));
      return;
    }

    if (!values.modelId || !values.associationProviderId) return;

    try {
      // Parse the association and provider IDs
      const [associationId, providerId] = values.associationProviderId.split(':').map(Number);
      
      if (!associationId || !providerId) {
        notify.error(new Error('Invalid provider configuration selected'));
        return;
      }

      // Find the selected association to get the identifier
      const selectedAssociation = associations?.find(a => a.associationId === associationId);
      if (!selectedAssociation) {
        notify.error(new Error('Selected configuration not found'));
        return;
      }
      
      const createData: CreateModelProviderMappingDto = {
        modelAlias: values.modelAlias,
        providerId: providerId,
        providerModelId: selectedAssociation.identifier, // Use the identifier from the association
        modelProviderTypeAssociationId: associationId,
        priority: values.priority,
        weight: 1,
        isEnabled: values.isEnabled,
      };

      await createMapping.mutateAsync(createData);
      form.reset();
      onSuccess?.();
      handleClose();
    } catch {
      // Error handling done by mutation
    }
  };

  const selectedModel = models.find(m => m.id === form.values.modelId);

  const modelOptions = models
    .filter(m => m.id !== undefined)
    .map(m => ({
      value: String(m.id),
      label: m.name ?? 'Unknown Model'
    }));

  return (
    <Modal
      opened={isOpen}
      onClose={handleClose}
      title="Create Model Provider Mapping"
      size="xl"
    >
      <form onSubmit={form.onSubmit(handleSubmit)}>
        <Stack gap="md">
          {/* Model Selection Section */}
          <Paper p="md" withBorder>
            <Stack gap="sm">
              <Text fw={600} size="sm">Model Configuration</Text>
              
              <Select
                label="Model"
                placeholder="Select a model"
                data={modelOptions}
                searchable
                required
                {...form.getInputProps('modelId')}
                onChange={(value: string | null) => {
                  form.setFieldValue('modelId', value ? parseInt(value, 10) : null);
                  // Auto-fill model alias if empty
                  if (!form.values.modelAlias && value) {
                    const model = models.find(m => m.id === parseInt(value, 10));
                    if (model?.name) {
                      form.setFieldValue('modelAlias', model.name);
                    }
                  }
                }}
                value={form.values.modelId?.toString()}
              />

              <TextInput
                label="Model Alias"
                placeholder="e.g., gpt-4, claude-3-opus"
                description="The name clients will use to request this model"
                required
                {...form.getInputProps('modelAlias')}
              />

              {selectedModel && (
                <Paper p="xs" withBorder>
                  <Text size="xs" fw={500} mb="xs">Model Capabilities:</Text>
                  <Flex gap="xs" wrap="wrap">
                    {selectedModel.supportsChat && <Badge size="sm" leftSection={<IconRobot size={12} />}>Chat</Badge>}
                    {selectedModel.supportsImageInput && <Badge size="sm" leftSection={<IconEye size={12} />}>Image Input</Badge>}
                    {selectedModel.supportsVideoInput && <Badge size="sm" leftSection={<IconVideo size={12} />}>Video Input</Badge>}
                    {selectedModel.supportsImageGeneration && <Badge size="sm" leftSection={<IconBrush size={12} />}>Images</Badge>}
                    {selectedModel.supportsVideoGeneration && <Badge size="sm" leftSection={<IconVideo size={12} />}>Video Gen</Badge>}
                    {selectedModel.supportsEmbeddings && <Badge size="sm" leftSection={<IconBrain size={12} />}>Embeddings</Badge>}
                  </Flex>
                  <Text size="xs" mt="xs">Max Input Tokens: {selectedModel.maxInputTokens?.toLocaleString()}</Text>
                </Paper>
              )}
            </Stack>
          </Paper>

          {/* Provider Configuration Section */}
          {form.values.modelId && (
            <Paper p="md" withBorder>
              <Stack gap="sm">
                <Text fw={600} size="sm">Provider Configuration</Text>
                <Text size="xs" c="dimmed">
                  Select from available provider configurations for this model.
                  Only providers with configured ModelProviderTypeAssociations are shown.
                </Text>
                
                {associationsLoading ? (
                  <Text size="sm" c="dimmed">Loading available configurations...</Text>
                ) : (
                  <AssociationProviderSelect
                    associations={associations ?? []}
                    value={form.values.associationProviderId}
                    onChange={(value) => form.setFieldValue('associationProviderId', value)}
                  />
                )}

                <Divider my="xs" />

                <Group grow>
                  <NumberInput
                    label="Priority"
                    description="Lower values have higher priority"
                    min={0}
                    max={1000}
                    {...form.getInputProps('priority')}
                  />

                  <Switch
                    label="Enabled"
                    description="Whether this mapping is active"
                    checked={form.values.isEnabled}
                    {...form.getInputProps('isEnabled')}
                  />
                </Group>
              </Stack>
            </Paper>
          )}

          <Alert icon={<IconAlertCircle size={16} />} color="blue">
            <Text size="sm" fw={500} mb="xs">Three-Layer Architecture:</Text>
            <Text size="xs">
              1. <strong>Model</strong>: The canonical model definition with capabilities
            </Text>
            <Text size="xs">
              2. <strong>ModelProviderTypeAssociation</strong>: Defines how a model runs on different provider types
            </Text>
            <Text size="xs">
              3. <strong>Provider</strong>: Actual configured instance with credentials
            </Text>
            <Text size="xs" mt="xs">
              All three must exist before a mapping can be created.
            </Text>
          </Alert>

          <Group justify="flex-end">
            <Button variant="subtle" onClick={handleClose}>
              Cancel
            </Button>
            <Button 
              type="submit" 
              loading={createMapping.isPending}
              disabled={modelsLoading || associationsLoading}
              onClick={() => {
                // Trigger validation to show errors on all fields
                form.validate();
              }}
            >
              Create Mapping
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
