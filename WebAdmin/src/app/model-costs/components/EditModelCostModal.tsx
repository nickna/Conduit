'use client';

import { Button, Group, Modal, Stack } from '@mantine/core';
import { useForm } from '@mantine/form';
import type { ModelCostDto } from '@/lib/admin-api';
import { useUpdateModelCost } from '../hooks/useModelCostsApi';
import {
  modelCostFormValidation,
  modelCostToFormValues,
  toUpdateModelCostDto,
  type ModelCostFormValues,
} from '../utils/modelCostForm';
import { ModelCostFormFields } from './ModelCostFormFields';

interface EditModelCostModalProps {
  isOpen: boolean;
  modelCost: ModelCostDto;
  onClose: () => void;
  onSuccess?: () => void;
}

export function EditModelCostModal({ isOpen, modelCost, onClose, onSuccess }: EditModelCostModalProps) {
  const updateMutation = useUpdateModelCost();
  const form = useForm<ModelCostFormValues>({
    initialValues: modelCostToFormValues(modelCost),
    validate: modelCostFormValidation,
  });

  return (
    <Modal opened={isOpen} onClose={onClose} title="Edit Model Pricing" size="xl">
      <form onSubmit={form.onSubmit(values => updateMutation.mutate(
        { id: modelCost.id, data: toUpdateModelCostDto(values) },
        { onSuccess: () => { onSuccess?.(); onClose(); } },
      ))}>
        <Stack gap="md">
          <ModelCostFormFields form={form} />
          <Group justify="flex-end" mt="xl">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button type="submit" loading={updateMutation.isPending}>Save Changes</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
