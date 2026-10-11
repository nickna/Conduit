'use client';

import { useState, useEffect } from 'react';
import { TextInput, NumberInput, Select, Switch, Textarea } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notify } from '@/lib/notifications';
import { withAdminClient } from '@/lib/client/adminClient';
import { useFormModal } from '@/hooks/useFormModal';
import { EntityFormModal } from '@/components/common/EntityFormModal';
import { ProviderType, normalizeProviderType, type CreateProviderTool, type ToolProviderOption } from '@/lib/admin-api';

interface CreateProviderToolModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}

export function CreateProviderToolModal({ isOpen, onClose, onSuccess }: CreateProviderToolModalProps) {
  const [providers, setProviders] = useState<ToolProviderOption[]>([]);
  const [billingUnits, setBillingUnits] = useState<string[]>([]);

  const form = useForm<CreateProviderTool>({
    initialValues: {
      provider: ProviderType.Unknown,
      toolName: '',
      toolParameters: null,
      costPerUnit: 0,
      billingUnit: '',
      costDescription: '',
      isActive: true,
    },
    validate: {
      provider: (value) => value === ProviderType.Unknown ? 'Provider is required' : null,
      toolName: (value) => !value?.trim() ? 'Tool name is required' : null,
      costPerUnit: (value) => value !== null && value !== undefined && value < 0 ? 'Cost must be non-negative' : null,
    },
  });

  const { loading, handleSubmit, handleClose } = useFormModal({
    form,
    onClose,
    onSuccess,
    submitAction: (values) =>
      withAdminClient(client =>
        client.providerTools.createProviderTool({
          ...values,
          toolParameters: values.toolParameters?.trim() ?? null,
          costDescription: values.costDescription?.trim() ?? null,
          billingUnit: values.billingUnit?.trim() ?? null,
        })
      ),
    successMessage: 'Successfully created provider tool',
  });

  useEffect(() => {
    const loadOptions = async () => {
      try {
        const [providersData, unitsData] = await Promise.all([
          withAdminClient(client => client.providerTools.getToolProviders()),
          withAdminClient(client => client.providerTools.getBillingUnits()),
        ]);
        setProviders(providersData);
        setBillingUnits(unitsData);
      } catch (error) {
        notify.error(error, 'Could not load provider and billing unit options');
      }
    };

    if (isOpen) {
      void loadOptions();
    }
  }, [isOpen]);

  return (
    <EntityFormModal
      opened={isOpen}
      onClose={handleClose}
      title="Add Provider Tool"
      size="md"
      onSubmit={form.onSubmit(handleSubmit)}
      loading={loading}
      submitLabel="Create Tool"
    >
      <Select
        label="Provider"
        placeholder="Select a provider"
        data={providers.map(p => ({
          value: p.value.toString(),
          label: p.name,
        }))}
        {...form.getInputProps('provider')}
        onChange={(value: string | null) => form.setFieldValue(
          'provider',
          value ? normalizeProviderType(value) ?? ProviderType.Unknown : ProviderType.Unknown
        )}
        value={form.values.provider.toString()}
        required
      />

      <TextInput
        label="Tool Name"
        placeholder="e.g., code_execution, web_search"
        {...form.getInputProps('toolName')}
        required
      />

      <Textarea
        label="Tool Parameters"
        placeholder="Optional JSON parameters"
        minRows={2}
        {...form.getInputProps('toolParameters')}
      />

      <NumberInput
        label="Cost Per Unit"
        placeholder="0.0001"
        min={0}
        decimalScale={8}
        step={0.0001}
        {...form.getInputProps('costPerUnit')}
      />

      <Select
        label="Billing Unit"
        placeholder="Select a billing unit"
        data={billingUnits}
        searchable
        {...form.getInputProps('billingUnit')}
      />

      <Textarea
        label="Cost Description"
        placeholder="Describe how the cost is calculated"
        {...form.getInputProps('costDescription')}
      />

      <Switch
        label="Active"
        {...form.getInputProps('isActive', { type: 'checkbox' })}
      />
    </EntityFormModal>
  );
}
