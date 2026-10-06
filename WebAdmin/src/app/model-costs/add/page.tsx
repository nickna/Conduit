'use client';

import { Box, Button, Container, Group, Paper, Stack, Text, Title } from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconArrowLeft } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { ModelCostFormFields } from '../components/ModelCostFormFields';
import { useCreateModelCost } from '../hooks/useModelCostsApi';
import {
  createModelCostFormValues,
  modelCostFormValidation,
  toCreateModelCostDto,
  type ModelCostFormValues,
} from '../utils/modelCostForm';

export default function AddModelCostPage() {
  const router = useRouter();
  const createMutation = useCreateModelCost();
  const form = useForm<ModelCostFormValues>({
    initialValues: createModelCostFormValues(),
    validate: modelCostFormValidation,
  });
  const close = () => router.push('/model-costs');

  return (
    <Container size="xl">
      <Box mb="xl">
        <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={close} mb="md">Back to Model Costs</Button>
        <Title order={2}>Add Model Pricing</Title>
        <Text c="dimmed" size="sm" mt={4}>Configure pricing for one or more model mappings.</Text>
      </Box>
      <form onSubmit={form.onSubmit(values => createMutation.mutate(toCreateModelCostDto(values), { onSuccess: close }))}>
        <Stack gap="lg">
          <Paper p="md" shadow="xs"><ModelCostFormFields form={form} /></Paper>
          <Group justify="flex-end">
            <Button variant="subtle" onClick={close}>Cancel</Button>
            <Button type="submit" loading={createMutation.isPending}>Create Pricing</Button>
          </Group>
        </Stack>
      </form>
    </Container>
  );
}
