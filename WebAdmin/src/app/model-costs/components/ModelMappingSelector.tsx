'use client';

import React, { useState } from 'react';
import {
  MultiSelect,
  Stack,
  Text,
  Badge,
  Group,
  Loader,
  Alert,
} from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { useModelMappings } from '@/hooks/useModelMappingsApi';

interface ModelMappingSelectorProps {
  value: number[];
  onChange: (value: number[]) => void;
  error?: string;
  required?: boolean;
  placeholder?: string;
  description?: string;
}

interface SelectItem {
  value: string;
  label: string;
  provider?: string;
  providerType?: string;
  modelId?: string;
}

export function ModelMappingSelector({
  value,
  onChange,
  error,
  required = false,
  placeholder = "Select models to apply this cost to",
  description = "Choose one or more models that will use this pricing configuration"
}: ModelMappingSelectorProps) {
  const { mappings, isLoading, error: loadError } = useModelMappings();
  const [searchValue, setSearchValue] = useState('');

  // Convert mappings to select items
  const selectItems: SelectItem[] = mappings.map((mapping) => {
    return {
      value: String(mapping.modelProviderTypeAssociationId),
      label: mapping.modelAlias,
      provider: mapping.provider?.displayName,
      providerType: mapping.provider?.providerType,
      modelId: mapping.providerModelId,
    };
  });

  // Group items by provider - removed as unused

  // Filter items based on search
  const filteredItems = selectItems.filter(item => {
    const search = searchValue.toLowerCase();
    return (
      item.label.toLowerCase().includes(search) ||
      (item.provider?.toLowerCase().includes(search) ?? false) ||
      (item.modelId?.toLowerCase().includes(search) ?? false)
    );
  });

  // Custom item component to show more details
  const ItemComponent = ({ label, provider, providerType, modelId }: SelectItem) => (
    <div>
      <Text size="sm" fw={500}>{label}</Text>
      <Group gap="xs">
        <Text size="xs" c="dimmed">{provider ?? ''}</Text>
        {providerType && (
          <Badge size="xs" variant="dot">{providerType}</Badge>
        )}
        {modelId && modelId !== label && (
          <Text size="xs" c="dimmed">({modelId})</Text>
        )}
      </Group>
    </div>
  );

  if (loadError) {
    return (
      <Alert icon={<IconInfoCircle size={16} />} color="red">
        Failed to load model mappings: {loadError.message}
      </Alert>
    );
  }

  if (isLoading) {
    return (
      <Stack gap="xs">
        <Text size="sm" c="dimmed">{description}</Text>
        <Group>
          <Loader size="sm" />
          <Text size="sm" c="dimmed">Loading available models...</Text>
        </Group>
      </Stack>
    );
  }

  if (mappings.length === 0) {
    return (
      <Alert icon={<IconInfoCircle size={16} />} color="yellow">
        No model mappings available. Please configure model mappings first.
      </Alert>
    );
  }

  return (
    <Stack gap="xs">
      <MultiSelect
        label="Associated Models"
        placeholder={placeholder}
        description={description}
        data={filteredItems}
        value={value.map(id => id.toString())}
        onChange={(values) => onChange(values.map(v => parseInt(v)))}
        searchable
        searchValue={searchValue}
        onSearchChange={setSearchValue}
        error={error}
        required={required}
        maxDropdownHeight={300}
        nothingFoundMessage="No models found"
        clearable
        renderOption={({ option }) => {
          const item = selectItems.find(i => i.value === option.value);
          return item ? <ItemComponent {...item} /> : option.label;
        }}
      />
      {value.length > 0 && (
        <Group gap="xs">
          <Text size="xs" c="dimmed">Selected: {value.length} model(s)</Text>
        </Group>
      )}
    </Stack>
  );
}
