'use client';

import { Group, NumberInput, Select, Stack, Switch, Tabs, Textarea, TextInput } from '@mantine/core';
import type { UseFormReturnType } from '@mantine/form';
import { IconCurrencyDollar, IconHeadphones, IconSettings } from '@tabler/icons-react';
import { ModelTypeUtils, PricingModel } from '@/lib/admin-api';
import type { ModelCostFormValues } from '../utils/modelCostForm';
import { ModelMappingSelector } from './ModelMappingSelector';
import { PricingModelSelector } from './PricingModelSelector';

interface ModelCostFormFieldsProps {
  form: UseFormReturnType<ModelCostFormValues>;
}

export function ModelCostFormFields({ form }: ModelCostFormFieldsProps) {
  const showStandardFields = form.values.pricingModel === PricingModel.Standard;

  return (
    <Stack gap="md">
      <TextInput label="Cost Name" placeholder="e.g., GPT-4 Turbo Standard Pricing" required {...form.getInputProps('costName')} />
      <ModelMappingSelector
        value={form.values.modelProviderTypeAssociationIds}
        onChange={ids => form.setFieldValue('modelProviderTypeAssociationIds', ids)}
        error={form.errors.modelProviderTypeAssociationIds as string}
        required
      />
      <Select label="Model Type" data={ModelTypeUtils.getSelectOptions()} {...form.getInputProps('modelType')} />
      <PricingModelSelector
        pricingModel={form.values.pricingModel}
        pricingConfiguration={form.values.pricingConfiguration}
        onPricingModelChange={model => form.setFieldValue('pricingModel', model)}
        onConfigurationChange={configuration => form.setFieldValue('pricingConfiguration', configuration)}
      />

      {showStandardFields && (
        <Tabs defaultValue="tokens">
          <Tabs.List>
            <Tabs.Tab value="tokens" leftSection={<IconCurrencyDollar size={16} />}>Token Costs</Tabs.Tab>
            <Tabs.Tab value="audio" leftSection={<IconHeadphones size={16} />}>Audio Costs</Tabs.Tab>
            <Tabs.Tab value="advanced" leftSection={<IconSettings size={16} />}>Advanced</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="tokens" pt="md">
            <Stack gap="md">
              <Group grow>
                <NumberInput label="Input Cost" description="Per million tokens (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('inputCostPerMillion')} />
                <NumberInput label="Output Cost" description="Per million tokens (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('outputCostPerMillion')} />
              </Group>
              <Group grow>
                <NumberInput label="Cached Input Cost" description="Per million tokens (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('cachedInputCostPerMillion')} />
                <NumberInput label="Cache Write Cost" description="Per million tokens (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('cachedInputWriteCostPerMillion')} />
              </Group>
              <NumberInput label="Embedding Cost" description="Per million tokens (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('embeddingCostPerMillion')} />
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="audio" pt="md">
            <Group grow>
              <NumberInput label="Audio Cost" description="Per minute (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('audioCostPerMinute')} />
              <NumberInput label="Audio Character Cost" description="Per 1,000 characters (USD)" min={0} decimalScale={6} leftSection={<IconCurrencyDollar size={16} />} {...form.getInputProps('audioCostPerKCharacters')} />
            </Group>
          </Tabs.Panel>

          <Tabs.Panel value="advanced" pt="md">
            <Stack gap="md">
              <Switch label="Supports Batch Processing" description="Enable batch processing discounts" {...form.getInputProps('supportsBatchProcessing', { type: 'checkbox' })} />
              {form.values.supportsBatchProcessing && (
                <NumberInput label="Batch Processing Multiplier" description="0.5 means a 50% discount" min={0} max={1} step={0.1} decimalScale={2} {...form.getInputProps('batchProcessingMultiplier')} />
              )}
              <NumberInput label="Priority" description="Higher priority costs are preferred" min={0} {...form.getInputProps('priority')} />
              <Textarea label="Description" placeholder="Additional notes about this pricing configuration" {...form.getInputProps('description')} />
              <Switch label="Active" description="Enable or disable this pricing configuration" {...form.getInputProps('isActive', { type: 'checkbox' })} />
            </Stack>
          </Tabs.Panel>
        </Tabs>
      )}
    </Stack>
  );
}
