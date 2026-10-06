'use client';

import { Alert, Button, Group, Modal, Stack } from '@mantine/core';
import { useForm } from '@mantine/form';
import { useEffect, useMemo } from 'react';
import { IconInfoCircle } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { ModelType, type ModelCostDto, type ModelDto } from '@/lib/admin-api';
import { ModelCostFormFields } from '@/app/model-costs/components/ModelCostFormFields';
import { useCreateModelCost, useUpdateModelCost } from '@/app/model-costs/hooks/useModelCostsApi';
import {
  createModelCostFormValues,
  modelCostFormValidation,
  modelCostToFormValues,
  toCreateModelCostDto,
  toUpdateModelCostDto,
  type ModelCostFormValues,
} from '@/app/model-costs/utils/modelCostForm';
import { extractCapabilities } from '@/utils/typeGuards';

interface ModelCostEditorModalProps {
  isOpen: boolean;
  model: ModelDto;
  existingCost?: ModelCostDto | null;
  onClose: () => void;
  onSuccess?: () => void;
}

function getModelType(model: ModelDto): ModelType {
  const capabilities = extractCapabilities(model);
  if (capabilities.supportsVideoGeneration) return ModelType.Video;
  if (capabilities.supportsImageGeneration) return ModelType.Image;
  if (capabilities.supportsEmbeddings) return ModelType.Embedding;
  return ModelType.Chat;
}

export function ModelCostEditorModal({
  isOpen,
  model,
  existingCost,
  onClose,
  onSuccess,
}: ModelCostEditorModalProps) {
  const createMutation = useCreateModelCost();
  const updateMutation = useUpdateModelCost();
  const queryClient = useQueryClient();
  const inferredModelType = useMemo(() => getModelType(model), [model]);
  const form = useForm<ModelCostFormValues>({
    initialValues: createModelCostFormValues(),
    validate: modelCostFormValidation,
  });

  useEffect(() => {
    if (!isOpen) return;
    const values = existingCost
      ? modelCostToFormValues(existingCost)
      : {
          ...createModelCostFormValues(),
          costName: `${model.name ?? 'Model'} Pricing`,
          modelType: inferredModelType,
        };
    form.setValues(values);
    form.resetDirty(values);
  // Form helpers are stable; reinitializing on their object identity would reset user edits.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [existingCost, inferredModelType, isOpen, model.name]);

  const complete = async () => {
    await queryClient.invalidateQueries({ queryKey: ['models'] });
    onSuccess?.();
    onClose();
  };

  const submit = (values: ModelCostFormValues) => {
    if (existingCost) {
      updateMutation.mutate(
        { id: existingCost.id, data: toUpdateModelCostDto(values) },
        { onSuccess: () => void complete() },
      );
    } else {
      createMutation.mutate(toCreateModelCostDto(values), { onSuccess: () => void complete() });
    }
  };

  const pending = createMutation.isPending || updateMutation.isPending;
  const error = createMutation.error ?? updateMutation.error;

  return (
    <Modal opened={isOpen} onClose={onClose} title={existingCost ? 'Edit Model Pricing' : 'Add Model Pricing'} size="xl">
      <form onSubmit={form.onSubmit(submit)}>
        <Stack gap="md">
          {error && (
            <Alert icon={<IconInfoCircle size={16} />} color="red">
              {error.message}
            </Alert>
          )}
          <ModelCostFormFields form={form} />
          <Group justify="flex-end">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button type="submit" loading={pending}>{existingCost ? 'Save Changes' : 'Create Pricing'}</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
